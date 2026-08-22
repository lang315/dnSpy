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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.Attach;
using dnSpy.Contracts.Debugger.DotNet.CorDebug;
using dnSpy.Contracts.Debugger.Steppers;
using dnSpy.MCP.Server;
using Newtonsoft.Json.Linq;
using static dnSpy.MCP.Tools.JsonUtils;

namespace dnSpy.MCP.Tools {
	/// <summary>Session control: status, start, attach, break/continue/stop/restart, step.</summary>
	sealed class DebugTools {
		readonly DbgAccess dbg;
		readonly Lazy<AttachableProcessesService> attachService;

		public DebugTools(DbgAccess dbg, Lazy<AttachableProcessesService> attachService) {
			this.dbg = dbg;
			this.attachService = attachService;
		}

		DbgManager Mgr => dbg.DbgManager;

		public IEnumerable<ToolDef> Create() {
			yield return new ToolDef("dbg_status",
				"Get the current debug session status: whether debugging, running/paused, and the list of debugged processes.",
				Schema.Object(),
				_ => Status(), readOnly: true);

			yield return new ToolDef("dbg_start",
				"Start debugging a .NET executable. Picks .NET (Core/5+) vs .NET Framework automatically; self-contained single-file exes are ambiguous, so pass 'runtime' to force the choice.",
				Schema.Object(
					("path", Schema.Str("Full path to the .exe or .dll to debug"), true),
					("args", Schema.Str("Command-line arguments passed to the program"), false),
					("working_dir", Schema.Str("Working directory (defaults to the program's directory)"), false),
					("break_at_entry", Schema.Bool("Break at the entry point (default false)"), false),
					("runtime", Schema.Str("Force runtime: 'net' (Core/5+) or 'netfx' (Framework)"), false)),
				Start, destructive: true);

			yield return new ToolDef("dbg_list_attachable",
				"List running .NET processes that can be attached to.",
				Schema.Object(
					("name", Schema.Str("Filter by process name (wildcards * and ? allowed)"), false)),
				ListAttachable, readOnly: true);

			yield return new ToolDef("dbg_attach",
				"Attach the debugger to a running .NET process by pid or process name.",
				Schema.Object(
					("pid", Schema.Int("Process id to attach to"), false),
					("name", Schema.Str("Process name to attach to (wildcards * and ? allowed)"), false)),
				Attach);

			yield return new ToolDef("dbg_set_thread",
				"Set the current thread, used as the default context for callstack/locals/eval.",
				Schema.Object(
					("thread_id", Schema.Int("Native thread id"), true)),
				SetThread);

			yield return new ToolDef("dbg_break",
				"Break (pause) all debugged processes.",
				Schema.Object(),
				_ => { Mgr.BreakAll(); return "break requested"; });

			yield return new ToolDef("dbg_continue",
				"Resume (run) all debugged processes.",
				Schema.Object(),
				_ => { Mgr.RunAll(); return "continue requested"; });

			yield return new ToolDef("dbg_stop",
				"Stop debugging: terminate all debugged processes.",
				Schema.Object(),
				_ => { Mgr.StopDebuggingAll(); return "stop requested"; }, destructive: true);

			yield return new ToolDef("dbg_restart",
				"Restart the current debug session.",
				Schema.Object(),
				_ => {
					if (!Mgr.CanRestart)
						throw new InvalidOperationException("cannot restart the current session");
					Mgr.Restart();
					return "restart requested";
				}, destructive: true);

			yield return new ToolDef("dbg_step",
				"Step the paused thread and wait for the step to complete. kind = into | over | out.",
				Schema.Object(
					("kind", Schema.Str("into, over, or out"), true),
					("timeout_ms", Schema.Int("Max time to wait for the step to complete (default 15000)"), false)),
				Step);

			yield return new ToolDef("dbg_wait_for_break",
				"Block until a debugged process pauses (breakpoint hit, step done, or break), or until timeout.",
				Schema.Object(
					("timeout_ms", Schema.Int("Max time to wait in ms (default 30000)"), false)),
				WaitForBreak, readOnly: true);
		}

		string Status() => dbg.Invoke(() => {
			var processes = new JArray(Mgr.Processes.Select(Process).Cast<object>().ToArray());
			return Json(new JObject {
				["isDebugging"] = Mgr.IsDebugging,
				["isRunning"] = Mgr.IsRunning,
				["debugTags"] = new JArray(Mgr.DebugTags.Cast<object>().ToArray()),
				["processes"] = processes,
			});
		});

		string Start(JObject args) {
			var path = (string?)args["path"] ?? throw new ArgumentException("'path' is required");
			if (!File.Exists(path))
				throw new FileNotFoundException($"file not found: {path}");
			// An apphost .exe re-execs the .NET host, which drops breakpoints armed before launch; debugging
			// the sibling .dll directly (the `dotnet exec` target) lets pre-set breakpoints bind on module load.
			if (string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)) {
				var dll = SiblingPath(path, ".dll");
				if (File.Exists(dll) && File.Exists(SiblingPath(path, ".runtimeconfig.json")))
					path = dll;
			}
			var cmdLine = (string?)args["args"];
			var workingDir = (string?)args["working_dir"] ?? Path.GetDirectoryName(Path.GetFullPath(path));
			var breakAtEntry = (bool?)args["break_at_entry"] ?? false;
			var runtime = (string?)args["runtime"];

			var breakKind = breakAtEntry ? PredefinedBreakKinds.EntryPoint : null;
			CorDebugStartDebuggingOptions options = IsDotNetCore(path, runtime)
				? new DotNetStartDebuggingOptions { Filename = path, CommandLine = cmdLine, WorkingDirectory = workingDir, BreakKind = breakKind }
				: new DotNetFrameworkStartDebuggingOptions { Filename = path, CommandLine = cmdLine, WorkingDirectory = workingDir, BreakKind = breakKind };

			// Start is called directly (not on the dbg dispatcher) — it boots that dispatcher itself.
			var err = Mgr.Start(options);
			if (err is not null)
				throw new InvalidOperationException(err);

			// Start only means "the engine accepted the request": IsDebugging flips to true immediately
			// while Processes is still empty. Returning here would hand back a success the caller cannot
			// act on, and a dbg_stop landing in that window finds nothing to stop and wedges the session
			// as debugging-with-no-processes, from which it never recovers. Wait for a real process so
			// "started" means started.
			if (!WaitForProcess(15000))
				throw new InvalidOperationException(
					$"the debug engine accepted {Path.GetFileName(path)} but no process appeared; the session may need dbg_stop");
			return $"started debugging {Path.GetFileName(path)}";
		}

		bool WaitForProcess(int timeoutMs) {
			var sw = Stopwatch.StartNew();
			while (sw.ElapsedMilliseconds < timeoutMs) {
				if (dbg.Invoke(() => Mgr.Processes.Length > 0))
					return true;
				Thread.Sleep(100);
			}
			return false;
		}

		static bool IsDotNetCore(string path, string? runtime) {
			if (string.Equals(runtime, "net", StringComparison.OrdinalIgnoreCase))
				return true;
			if (string.Equals(runtime, "netfx", StringComparison.OrdinalIgnoreCase))
				return false;
			// Heuristic: a .runtimeconfig.json next to the file means .NET Core/5+; a bare .dll needs the host too.
			if (File.Exists(SiblingPath(path, ".runtimeconfig.json")))
				return true;
			return string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase);
		}

		// The file next to <paramref name="path"/> with the same base name and the given extension.
		static string SiblingPath(string path, string extension) =>
			Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileNameWithoutExtension(path) + extension);

		// dnSpy's attachable-process enumeration throws an AV when given a name filter (it inspects every
		// process's modules); fetch the full list unfiltered — that path is safe — and match name/pid ourselves.
		AttachableProcess[] GetAttachable(string? name, int? pid) {
			var all = attachService.Value.GetAttachableProcessesAsync(
				null, null, null, CancellationToken.None).GetAwaiter().GetResult();
			IEnumerable<AttachableProcess> q = all;
			if (pid is not null)
				q = q.Where(p => p.ProcessId == pid.Value);
			if (name is not null) {
				var rx = WildcardRegex(name);
				q = q.Where(p => rx.IsMatch(p.Name));
			}
			return q.ToArray();
		}

		static Regex WildcardRegex(string pattern) =>
			new Regex("^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase);

		string ListAttachable(JObject args) {
			var name = (string?)args["name"];
			var processes = GetAttachable(name, null);
			var arr = new JArray(processes.Select(p => (object)new JObject {
				["pid"] = p.ProcessId,
				["name"] = p.Name,
				["title"] = p.Title,
				["runtime"] = p.RuntimeName,
				["architecture"] = p.Architecture.ToString(),
			}).ToArray());
			return Json(arr);
		}

		string SetThread(JObject args) {
			var threadId = (ulong)(long)(args["thread_id"] ?? throw new ArgumentException("'thread_id' is required"));
			return dbg.Invoke(() => {
				foreach (var p in Mgr.Processes)
					foreach (var t in p.Threads)
						if (t.Id == threadId) {
							Mgr.CurrentThread.Current = t;
							return $"current thread set to {threadId}";
						}
				throw new InvalidOperationException($"no thread with id {threadId}");
			});
		}

		string Attach(JObject args) {
			var pid = (int?)args["pid"];
			var name = (string?)args["name"];
			if (pid is null && name is null)
				throw new ArgumentException("provide 'pid' or 'name'");

			var processes = GetAttachable(name, pid);
			if (processes.Length == 0)
				throw new InvalidOperationException("no matching attachable process found");
			var target = processes[0];
			target.Attach();
			return $"attaching to pid {target.ProcessId} ({target.Name}, {target.RuntimeName})";
		}

		string Step(JObject args) {
			var kindStr = (string?)args["kind"] ?? throw new ArgumentException("'kind' is required");
			var kind = kindStr.ToLowerInvariant() switch {
				"into" => DbgStepKind.StepInto,
				"over" => DbgStepKind.StepOver,
				"out" => DbgStepKind.StepOut,
				_ => throw new ArgumentException("'kind' must be into, over, or out"),
			};
			var timeout = Clamp((int?)args["timeout_ms"] ?? 15000, 0, 300000);

			// Not disposed: the StepComplete lambda may still fire after a timeout and call Set().
			// ManualResetEventSlim.Set() after the waiter timed out is a benign no-op; the object is
			// released once the auto-close stepper drops the callback on the next continue.
			var done = new ManualResetEventSlim(false);
			string? stepError = null;

			dbg.Invoke(() => {
				var thread = Mgr.CurrentThread.Current;
				if (thread is null || thread.Process.State != DbgProcessState.Paused)
					throw new InvalidOperationException("no paused thread; break first");
				var stepper = thread.CreateStepper();
				stepper.StepComplete += (s, e) => {
					stepError = e.Error;
					done.Set();
				};
				stepper.Step(kind, autoClose: true);
				return 0;
			});

			if (!done.Wait(timeout))
				return "step started but did not complete before timeout";
			if (stepError is not null)
				throw new InvalidOperationException($"step failed: {stepError}");
			return TopFrameSummary();
		}

		string WaitForBreak(JObject args) {
			var timeout = Clamp((int?)args["timeout_ms"] ?? 30000, 0, 300000);
			var sw = Stopwatch.StartNew();
			while (sw.ElapsedMilliseconds < timeout) {
				// Read engine state on the dispatcher thread, like every other debugger read.
				if (dbg.Invoke(() => Mgr.IsDebugging && Mgr.IsRunning == false))
					return TopFrameSummary();
				Thread.Sleep(100);
			}
			return "timeout: no process paused";
		}

		string TopFrameSummary() => dbg.Invoke(() => {
			var thread = Mgr.CurrentThread.Current;
			if (thread is null)
				return "paused (no current thread)";
			var frame = thread.GetTopStackFrame();
			var obj = new JObject {
				["paused"] = true,
				["thread"] = ThreadJson(thread),
				["topFrame"] = frame is null ? null : new JObject {
					["module"] = frame.Module?.Name,
					["token"] = frame.HasFunctionToken ? "0x" + frame.FunctionToken.ToString("X8") : null,
					["offset"] = "0x" + frame.FunctionOffset.ToString("X"),
				},
			};
			return Json(obj);
		});
	}
}
