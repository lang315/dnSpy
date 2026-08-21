/*
    Copyright (C) 2014-2019 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.Attach;
using dnSpy.Contracts.Debugger.Breakpoints.Code;
using dnSpy.Contracts.Debugger.Breakpoints.Modules;
using dnSpy.Contracts.Debugger.DotNet.Breakpoints.Code;
using dnSpy.Contracts.Debugger.DotNet.Code;
using dnSpy.Contracts.Debugger.Evaluation;
using dnSpy.Contracts.Debugger.Exceptions;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Metadata;
using dnSpy.MCP.Server;
using dnSpy.MCP.Tools;
using Newtonsoft.Json.Linq;

namespace dnSpy.MCP {
	/// <summary>
	/// Owns the MCP HTTP server lifetime. Started from an <c>[ExportAutoLoaded]</c> loader at app
	/// startup and stopped when the app exits. Composes the debugger MEF services into the tool set.
	/// </summary>
	[Export]
	sealed class McpServerHost {
		const int DefaultPort = 27115;

		readonly Lazy<DbgManager> dbgManager;
		readonly Lazy<AttachableProcessesService> attachService;
		readonly Lazy<DbgCodeBreakpointsService> bpService;
		readonly Lazy<DbgDotNetBreakpointFactory> bpFactory;
		readonly Lazy<DbgCodeBreakpointHitCountService> hitCountService;
		readonly Lazy<DbgLanguageService> languageService;
		readonly Lazy<DbgDotNetCodeLocationFactory> codeLocationFactory;
		readonly Lazy<IModuleIdProvider> moduleIdProvider;
		readonly Lazy<IDsDocumentService> documentService;
		readonly Lazy<DbgExceptionSettingsService> exceptionService;
		readonly Lazy<DbgModuleBreakpointsService> moduleBpService;
		readonly Lazy<IDecompilerService> decompilerService;

		McpServer? server;

		[ImportingConstructor]
		McpServerHost(Lazy<DbgManager> dbgManager, Lazy<AttachableProcessesService> attachService,
			Lazy<DbgCodeBreakpointsService> bpService, Lazy<DbgDotNetBreakpointFactory> bpFactory,
			Lazy<DbgCodeBreakpointHitCountService> hitCountService, Lazy<DbgLanguageService> languageService,
			Lazy<DbgDotNetCodeLocationFactory> codeLocationFactory, Lazy<IModuleIdProvider> moduleIdProvider,
			Lazy<IDsDocumentService> documentService, Lazy<DbgExceptionSettingsService> exceptionService,
			Lazy<DbgModuleBreakpointsService> moduleBpService, Lazy<IDecompilerService> decompilerService) {
			this.dbgManager = dbgManager;
			this.attachService = attachService;
			this.bpService = bpService;
			this.bpFactory = bpFactory;
			this.hitCountService = hitCountService;
			this.languageService = languageService;
			this.codeLocationFactory = codeLocationFactory;
			this.moduleIdProvider = moduleIdProvider;
			this.documentService = documentService;
			this.exceptionService = exceptionService;
			this.moduleBpService = moduleBpService;
			this.decompilerService = decompilerService;
		}

		public void Start() {
			if (server is not null)
				return;

			try {
				Log("starting MCP server");
				// Authentication is on by default; TokenStore generates and persists a token on first
				// run. Anchored to the settings file so --settings-file isolates the token too.
				var auth = TokenStore.Resolve(AppDirectories.SettingsFilename, Log);
				var port = GetPort();
				var dbg = new DbgAccess(dbgManager.Value);
				// Created once here so the same collector both subscribes to MessageBoundBreakpoint (in
				// Start/Stop, like ProcessPaused) and backs the trace_log tool.
				var traceColl = new TraceCollector(dbg, languageService);
				var tools = new List<ToolDef>();
				tools.AddRange(new DebugTools(dbg, attachService).Create());
				tools.AddRange(new BreakpointTools(dbg, bpService, bpFactory, hitCountService, codeLocationFactory, moduleIdProvider, documentService).Create());
				tools.AddRange(new TracepointTools(dbg, bpService, bpFactory, hitCountService, codeLocationFactory, moduleIdProvider, documentService, traceColl).Create());
				tools.AddRange(new InspectionTools(dbg, languageService, codeLocationFactory).Create());
				tools.AddRange(new MemoryTools(dbg).Create());
				tools.AddRange(new ExceptionTools(dbg, exceptionService).Create());
				tools.AddRange(new ModuleBreakpointTools(dbg, moduleBpService).Create());
				// Static analysis: no debug engine, so these are constructed with only the document and
				// decompiler services and run off the dispatcher.
				tools.AddRange(new StaticTools(documentService, decompilerService).Create());
				// dump_module reaches into the *paused* debuggee and copies a module's image out of process
				// memory to disk, sizing it from the in-memory section table so a packed module is not
				// truncated — the StaticTools above then analyse the dump. Only DbgAccess is needed: it finds
				// the module and reads its process memory (all on the DbgManager dispatcher).
				tools.AddRange(new LiveModuleTools(dbg).Create());
				// decrypt_strings straddles both halves: static call-site discovery (documentService, like
				// StaticTools) plus func-eval in the paused process (dbg + languageService, like dbg_eval).
				tools.AddRange(new DecryptTools(dbg, documentService, languageService).Create());
				// heap_stats/find/object walk the paused debuggee's managed GC heap with ClrMD passive —
				// the same coexisting-with-CorDebug attach dnSpy's own DAC provider uses. Only a quick
				// {pid,bitness,paused} snapshot goes through the dispatcher (DbgAccess); the multi-second
				// heap walk runs on the request thread so it neither blocks the dispatcher nor hits its timeout.
				tools.AddRange(new HeapTools(dbg).Create());
				// Counts the live list rather than a snapshot, so the reported total covers every tool
				// including this one — the count is not knowable while the list is still being built.
				tools.AddRange(new InfoTools(port, () => auth.Token is not null,
					() => auth.Source,
					() => tools.Count).Create());

				var srv = new McpServer(port, tools, Log, auth.Token, auth.FilePath);
				srv.Start();
				server = srv;
				// Push a notification to SSE clients whenever a process pauses (breakpoint/step/break).
				var mgr = dbgManager.Value;
				mgr.ProcessPaused += OnProcessPaused;
				pausedManager = mgr;
				// Start collecting tracepoint hits (auto-resumed breakpoints that log) for trace_log.
				traceColl.Subscribe();
				traceCollector = traceColl;
			}
			catch (Exception ex) {
				// Port in use / listener denied / a debugger service failed to compose — log and stay off;
				// the extension itself is still loaded.
				Log("failed to start MCP server: " + ex);
			}
		}

		public void Stop() {
			if (traceCollector is not null) {
				traceCollector.Unsubscribe();
				traceCollector = null;
			}
			if (pausedManager is not null) {
				pausedManager.ProcessPaused -= OnProcessPaused;
				pausedManager = null;
			}
			server?.Stop();
			server = null;
		}

		DbgManager? pausedManager;
		TraceCollector? traceCollector;

		void OnProcessPaused(object? sender, ProcessPausedEventArgs e) {
			var srv = server;
			if (srv is null)
				return;
			srv.Broadcast("notifications/paused", new JObject {
				["pid"] = e.Process.Id,
				["threadId"] = e.Thread is null ? null : (long)e.Thread.Id,
			});
		}

		static int GetPort() {
			var env = Environment.GetEnvironmentVariable("DNSPY_MCP_PORT");
			if (int.TryParse(env, out var port) && port > 0 && port <= 65535)
				return port;
			return DefaultPort;
		}

		static void Log(string message) {
			Debug.WriteLine("[dnSpy.MCP] " + message);
			// Also append to a log file so the server's status is observable without a debugger attached.
			try {
				var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dnSpy.MCP.log");
				System.IO.File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss") + " " + message + Environment.NewLine);
			}
			catch { }
		}
	}
}
