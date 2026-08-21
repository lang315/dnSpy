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
using System.Globalization;
using System.Linq;
using System.Text;
using dnlib.DotNet;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.Breakpoints.Code;
using dnSpy.Contracts.Debugger.DotNet.Breakpoints.Code;
using dnSpy.Contracts.Debugger.DotNet.Code;
using dnSpy.Contracts.Debugger.Evaluation;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Metadata;
using dnSpy.MCP.Server;
using Newtonsoft.Json.Linq;
using static dnSpy.MCP.Tools.JsonUtils;

namespace dnSpy.MCP.Tools {
	/// <summary>One logged tracepoint hit. Location fields are captured on the debugger thread at hit time.</summary>
	sealed class TraceRecord {
		public long Seq;
		public DateTime TimestampUtc;
		public int BreakpointId;
		public ulong? ThreadId;
		public string? Module;
		public string? Token;
		public string? Location;
		public string Message = string.Empty;
		public bool Evaluated;
	}

	/// <summary>
	/// Collects tracepoint hits into a bounded ring buffer so an agent can read them back with the
	/// trace_log tool. dnSpy formats a tracepoint's message only for its own internal
	/// <c>ITracepointMessageListener</c>s, which are not reachable from here, so this subscribes to the
	/// contract event <see cref="DbgManager.MessageBoundBreakpoint"/> and does its own capture + {expr}
	/// interpolation.
	///
	/// The handler runs on <see cref="DbgManager.Dispatcher"/> (the event is raised there) and must never
	/// set <c>e.Pause</c> or throw: dnSpy owns the resume decision (Pause is monotonic-OR, so observing is
	/// safe and order-independent), and a throw would break the engine's own break/trace handling for the
	/// same hit.
	/// </summary>
	sealed class TraceCollector {
		// Cap the buffer so a hot tracepoint cannot grow memory without bound; oldest records drop first.
		const int Capacity = 1000;

		readonly DbgAccess dbg;
		readonly FrameEvaluator frameEvaluator;
		readonly object gate = new object();
		readonly Queue<TraceRecord> buffer = new Queue<TraceRecord>();
		long nextSeq;
		long dropped;
		bool subscribed;

		public TraceCollector(DbgAccess dbg, Lazy<DbgLanguageService> languageService) {
			this.dbg = dbg;
			// Same eval plumbing dbg_eval uses, so a tracepoint {expr} resolves values identically.
			frameEvaluator = new FrameEvaluator(dbg, languageService);
		}

		/// <summary>Start observing bound-breakpoint hits. Mirrors how the host wires ProcessPaused.</summary>
		public void Subscribe() {
			if (subscribed)
				return;
			dbg.DbgManager.MessageBoundBreakpoint += OnMessageBoundBreakpoint;
			subscribed = true;
		}

		/// <summary>Stop observing. Called when the server stops.</summary>
		public void Unsubscribe() {
			if (!subscribed)
				return;
			dbg.DbgManager.MessageBoundBreakpoint -= OnMessageBoundBreakpoint;
			subscribed = false;
		}

		void OnMessageBoundBreakpoint(object? sender, DbgMessageBoundBreakpointEventArgs e) {
			// On the dispatcher. Never throw (see class remarks) — swallow everything.
			try {
				var bp = e.BoundBreakpoint.Breakpoint;
				// Only tracepoints: a Trace whose Continue=true logs and auto-resumes. Everything else
				// (plain breakpoints, break-on-hit trace) is left entirely to dnSpy. We do NOT touch e.Pause.
				if (bp.Trace is not { Continue: true } trace)
					return;

				var thread = e.Thread;
				var message = BuildMessage(trace.Message ?? string.Empty, thread, out var evaluated);
				var record = new TraceRecord {
					TimestampUtc = DateTime.UtcNow,
					BreakpointId = bp.Id,
					ThreadId = thread?.Id,
					Message = message,
					Evaluated = evaluated,
				};
				// The location has to be read here, on the debugger thread.
				if (bp.Location is DbgDotNetCodeLocation loc) {
					record.Module = loc.Module.ModuleName;
					record.Token = "0x" + loc.Token.ToString("X8");
				}
				else
					record.Location = bp.Location.Type;
				Append(record);
			}
			catch {
				// A tracepoint observer must never disrupt the debugger's own message handling.
			}
		}

		void Append(TraceRecord record) {
			lock (gate) {
				record.Seq = nextSeq++;
				buffer.Enqueue(record);
				while (buffer.Count > Capacity) {
					buffer.Dequeue();
					dropped++;
				}
			}
		}

		/// <summary>
		/// Returns the most recent <paramref name="max"/> records (oldest-first among them) and the number
		/// dropped to overflow so far. When <paramref name="clear"/> is true the whole buffer is emptied
		/// and the dropped counter reset after the snapshot is taken.
		/// </summary>
		public (List<TraceRecord> entries, long dropped) Read(int max, bool clear) {
			lock (gate) {
				var all = buffer.ToArray(); // oldest -> newest
				var start = all.Length > max ? all.Length - max : 0;
				var entries = new List<TraceRecord>(all.Length - start);
				for (int i = start; i < all.Length; i++)
					entries.Add(all[i]);
				var droppedNow = dropped;
				if (clear) {
					buffer.Clear();
					dropped = 0;
				}
				return (entries, droppedNow);
			}
		}

		// --- Message formatting (v1) --------------------------------------------------------------------
		// The message is plain text plus {expr} placeholders, evaluated against the hit thread's top frame;
		// \{ and \} are literal braces. dnSpy's $CALLER/$FUNCTION/$TID/... macros are intentionally NOT
		// supported here — plain text + {expr} is the v1 scope.

		string BuildMessage(string template, DbgThread? thread, out bool evaluated) {
			if (thread is null && HasExpression(template)) {
				// Structural fallback: no thread was reported for this hit, so {expr} cannot be resolved.
				// Store the template verbatim and flag it, rather than emit an all-{!err} string.
				evaluated = false;
				return template;
			}
			evaluated = true;
			return Interpolate(template, thread);
		}

		static bool HasExpression(string template) {
			for (int i = 0; i < template.Length; i++) {
				if (template[i] == '\\' && i + 1 < template.Length && (template[i + 1] == '{' || template[i + 1] == '}')) {
					i++;
					continue;
				}
				if (template[i] == '{')
					return true;
			}
			return false;
		}

		string Interpolate(string template, DbgThread? thread) {
			var sb = new StringBuilder(template.Length);
			int i = 0;
			while (i < template.Length) {
				var c = template[i];
				if (c == '\\' && i + 1 < template.Length && (template[i + 1] == '{' || template[i + 1] == '}')) {
					sb.Append(template[i + 1]);
					i += 2;
					continue;
				}
				if (c == '{') {
					var end = template.IndexOf('}', i + 1);
					if (end < 0) {
						// Unterminated '{': treat the remainder as literal text.
						sb.Append(template, i, template.Length - i);
						break;
					}
					var expr = template.Substring(i + 1, end - i - 1);
					string formatted;
					try {
						// thread is non-null here: BuildMessage routes the no-thread-with-expr case to the
						// raw-template fallback before Interpolate is ever reached.
						formatted = frameEvaluator.EvaluateAtTracepoint(thread!, expr);
					}
					catch {
						// One unresolvable segment must not sink the whole message.
						formatted = "{!err}";
					}
					sb.Append(formatted);
					i = end + 1;
					continue;
				}
				sb.Append(c);
				i++;
			}
			return sb.ToString();
		}
	}

	/// <summary>
	/// The tracepoint tool group: bp_add_trace creates a tracepoint (a code breakpoint whose Trace is set
	/// with Continue=true) and trace_log reads back what those tracepoints have logged via the shared
	/// <see cref="TraceCollector"/>.
	/// </summary>
	sealed class TracepointTools {
		readonly DbgAccess dbg;
		readonly Lazy<DbgCodeBreakpointsService> bpService;
		readonly Lazy<DbgDotNetBreakpointFactory> bpFactory;
		readonly Lazy<DbgCodeBreakpointHitCountService> hitCountService;
		readonly Lazy<DbgDotNetCodeLocationFactory> codeLocationFactory;
		readonly Lazy<IModuleIdProvider> moduleIdProvider;
		readonly Lazy<IDsDocumentService> documentService;
		readonly TraceCollector collector;

		public TracepointTools(DbgAccess dbg, Lazy<DbgCodeBreakpointsService> bpService,
			Lazy<DbgDotNetBreakpointFactory> bpFactory, Lazy<DbgCodeBreakpointHitCountService> hitCountService,
			Lazy<DbgDotNetCodeLocationFactory> codeLocationFactory, Lazy<IModuleIdProvider> moduleIdProvider,
			Lazy<IDsDocumentService> documentService, TraceCollector collector) {
			this.dbg = dbg;
			this.bpService = bpService;
			this.bpFactory = bpFactory;
			this.hitCountService = hitCountService;
			this.codeLocationFactory = codeLocationFactory;
			this.moduleIdProvider = moduleIdProvider;
			this.documentService = documentService;
			this.collector = collector;
		}

		public IEnumerable<ToolDef> Create() {
			yield return new ToolDef("bp_add_trace",
				"Add a tracepoint: a breakpoint that, when hit, logs a message and AUTO-RESUMES instead of pausing. Identify the location by module+token, or by method name (sets a tracepoint on every overload). The message is plain text with {expr} placeholders evaluated against the hit frame (use \\{ and \\} for literal braces); read what tracepoints have logged with trace_log. The module must be a full path or already open in dnSpy; tracepoints can be set before the process starts.",
				Schema.Object(
					("module", Schema.Str("Module file path, or file name if already open in dnSpy"), true),
					("message", Schema.Str("Log message: plain text plus {expr} placeholders evaluated at each hit"), true),
					("method", Schema.Str("Fully-qualified method, e.g. 'MyApp.Program.Main' (every overload)"), false),
					("token", Schema.Str("Method metadata token, hex (0x06000001) or decimal (use instead of 'method')"), false),
					("il_offset", Schema.Str("IL offset into the method body, hex or decimal (default 0; only with 'token')"), false),
					("condition", Schema.Str("C#/VB condition expression; only log when true"), false),
					("enabled", Schema.Bool("Whether the tracepoint is enabled (default true)"), false)),
				AddTrace);

			yield return new ToolDef("trace_log",
				"Read the records logged by tracepoints (see bp_add_trace). Returns the most recent entries, each with a sequence number, timestamp, breakpoint id, thread id and the formatted message. The collector is a bounded ring buffer; once full the oldest records are dropped and counted in 'dropped'.",
				Schema.Object(
					("max", Schema.Int("Max entries to return (default 200, clamped 1-1000)"), false),
					("clear", Schema.Bool("Empty the buffer after returning its contents (default false)"), false)),
				TraceLog);
		}

		string AddTrace(JObject args) {
			var module = (string?)args["module"] ?? throw new ArgumentException("'module' is required");
			var message = (string?)args["message"] ?? throw new ArgumentException("'message' is required");
			var methodName = (string?)args["method"];
			var tokenStr = (string?)args["token"];
			var offset = args["il_offset"] is { } o ? ParseUInt((string?)o, "il_offset") : 0u;
			var condition = (string?)args["condition"];
			var enabled = (bool?)args["enabled"] ?? true;

			return dbg.Invoke(() => {
				// Build settings through the exact same path the other breakpoint tools use, then mark it a
				// tracepoint: Trace with Continue=true makes dnSpy log the message and auto-resume rather
				// than break. dnSpy's engine evaluates the {expr} message and performs the resume; this tool
				// only sets the flag (and the collector captures the message independently).
				var settings = BreakpointTools.MakeSettings(enabled, condition);
				settings.Trace = new DbgCodeBreakpointTrace(message, true);

				if (tokenStr is not null) {
					var moduleId = moduleIdProvider.Value.Create(ResolveModuleDef(module));
					var bp = bpFactory.Value.Create(moduleId, ParseUInt(tokenStr, "token"), offset, settings)
						?? throw new InvalidOperationException("a breakpoint already exists at that location");
					return Json(Describe(bp));
				}
				if (!string.IsNullOrEmpty(methodName)) {
					var moduleDef = ResolveModuleDef(module);
					var methods = MetadataResolver.ResolveMethods(moduleDef, methodName!);
					var infos = methods.Select(m => new DbgCodeBreakpointInfo(
						codeLocationFactory.Value.Create(moduleIdProvider.Value.Create(m.Module), m.MDToken.Raw, 0), settings)).ToArray();
					var bps = bpService.Value.Add(infos);
					if (bps.Length == 0)
						throw new InvalidOperationException($"all {infos.Length} matching overload(s) already have a breakpoint");
					return Json(new JArray(bps.Select(Describe).Cast<object>().ToArray()));
				}
				throw new ArgumentException("provide 'method' or 'token'");
			});
		}

		string TraceLog(JObject args) {
			var max = Clamp((int?)args["max"] ?? 200, 1, 1000);
			var clear = (bool?)args["clear"] ?? false;
			var (entries, dropped) = collector.Read(max, clear);
			var arr = new JArray();
			foreach (var r in entries) {
				var o = new JObject {
					["seq"] = r.Seq,
					["timestamp"] = r.TimestampUtc.ToString("o", CultureInfo.InvariantCulture),
					["breakpointId"] = r.BreakpointId,
					["threadId"] = r.ThreadId is null ? null : (long)r.ThreadId.Value,
					["message"] = r.Message,
					["evaluated"] = r.Evaluated,
				};
				if (r.Module is not null)
					o["module"] = r.Module;
				if (r.Token is not null)
					o["token"] = r.Token;
				if (r.Location is not null)
					o["location"] = r.Location;
				arr.Add(o);
			}
			return Json(new JObject {
				["count"] = entries.Count,
				["dropped"] = dropped,
				["entries"] = arr,
			});
		}

		ModuleDef ResolveModuleDef(string module) =>
			MetadataResolver.ResolveModule(documentService.Value, module);

		JObject Describe(DbgCodeBreakpoint bp) {
			var o = new JObject {
				["id"] = bp.Id,
				["enabled"] = bp.IsEnabled,
				["hitCount"] = hitCountService.Value.GetHitCount(bp),
				["boundCount"] = bp.BoundBreakpoints.Length,
			};
			if (bp.Location is DbgDotNetCodeLocation loc) {
				o["module"] = loc.Module.ModuleName;
				o["token"] = "0x" + loc.Token.ToString("X8");
				o["offset"] = "0x" + loc.Offset.ToString("X");
			}
			else
				o["location"] = bp.Location.Type;
			if (bp.Condition is { } cond)
				o["condition"] = cond.Condition;
			if (bp.Trace is { } trace) {
				o["trace"] = trace.Message;
				o["tracepoint"] = trace.Continue;
			}
			return o;
		}
	}
}
