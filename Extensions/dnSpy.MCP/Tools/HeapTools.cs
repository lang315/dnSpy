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
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using dnSpy.Contracts.Debugger;
using dnSpy.MCP.Server;
using Newtonsoft.Json.Linq;
using static dnSpy.MCP.Tools.JsonUtils;

namespace dnSpy.MCP.Tools {
	/// <summary>
	/// Walks the managed GC heap of the *paused* debuggee (heap_stats / heap_find / heap_object) by
	/// spawning an out-of-process ClrMD 4.x helper (<c>dnSpy.MCP.HeapHelper.exe</c>) against the very
	/// process CorDebug is debugging.
	///
	/// Why out-of-process: dnSpy's engine is built against ClrMD 1.1, and the bin dir ships exactly one
	/// <c>Microsoft.Diagnostics.Runtime.dll</c> — but ClrMD 1.1 cannot walk a .NET 6+ "regions" GC heap
	/// (it sees a handful of system types and zero user objects). ClrMD 4.x walks it fully. Since 4.x
	/// cannot be referenced in-process without colliding with the engine's 1.1, the walk lives in a tiny
	/// separate process. ClrMD reads the debuggee's DAC in-process, so the helper's bitness must match
	/// the debuggee's; the win-x64 and win-x86 helpers are published next to this extension and the
	/// matching one is launched per call.
	///
	/// Threading: a quick {pid, bitness, paused, exists} snapshot is taken on
	/// <see cref="DbgManager.Dispatcher"/> via <see cref="DbgAccess.Invoke"/> (it touches debugger
	/// objects and is instant). The helper is then spawned and awaited on the tool's OWN calling thread
	/// (the per-request HTTP worker), never the dispatcher — a multi-second walk must not block the
	/// single debugger dispatcher thread nor trip its marshalling timeout.
	///
	/// Safety: reads only while Paused (a static heap); the helper attaches passively (suspend:false) so
	/// it coexists with the live ICorDebug attach and never suspends the target; every walk is capped in
	/// the helper (reporting <c>truncated</c>); and the process is killed if it overruns the timeout.
	/// .NET Framework and .NET/CoreCLR only — Mono/Unity is not covered.
	/// </summary>
	sealed class HeapTools {
		// Clamp for heap_stats 'top' — high enough that a request for "all types" returns every distinct
		// type (a real heap has at most a few thousand), low enough to bound the response.
		const int MaxTopTypes = 100_000;
		// Clamp for heap_find 'max' — how many instances get per-object detail (the count is always exact).
		const int MaxInstances = 10_000;
		// Generous wait for one heap walk; the helper is killed if it overruns (huge heap / resumed target).
		const int HelperTimeoutMs = 60_000;

		readonly DbgAccess dbg;

		public HeapTools(DbgAccess dbg) => this.dbg = dbg;

		DbgManager Mgr => dbg.DbgManager;

		public IEnumerable<ToolDef> Create() {
			yield return new ToolDef("heap_stats",
				"Walk the managed GC heap of the paused debuggee and return a per-type histogram: for each type its object count and total bytes, sorted by total bytes descending, plus the overall object and byte totals. Requires a paused .NET/CoreCLR process (hit a breakpoint or call dbg_break). Reads the target's memory directly via ClrMD passive attach — it never suspends or resumes the process and does not use the debugger APIs, so it coexists with the live debug session. The dnSpy build's bitness must match the target (run the x86 build to inspect a 32-bit process). Not supported for Mono/Unity. Large heaps are capped: 'truncated' is set when the scan stopped early.",
				Schema.Object(
					("top", Schema.Int("Maximum number of types to return, highest total bytes first (default 100)"), false)),
				HeapStats, readOnly: true);

			yield return new ToolDef("heap_find",
				"Find every live instance of a managed type on the paused debuggee's heap. 'type' is a full type name (e.g. System.String); it matches exactly by default, or as a prefix if it ends with '*' (e.g. 'System.Collections.*'). Returns the true match 'count' and, for up to 'max' instances, each object's address, size and a small summary of its scalar/string fields (reference fields are shown as an address). An unknown type is NOT an error — it returns count 0. Requires a paused .NET/CoreCLR process; the dnSpy build's bitness must match the target. Reads memory directly (ClrMD passive), never suspending the target.",
				Schema.Object(
					("type", Schema.Str("Full type name to find; exact match, or a prefix match if it ends with '*'"), true),
					("max", Schema.Int("Maximum number of instances to detail (default 200); 'count' still reports the true total"), false)),
				HeapFind, readOnly: true);

			yield return new ToolDef("heap_object",
				"Inspect a single managed object on the paused debuggee's heap by its address (hex '0x...' or decimal). Returns its type, size, whether it is an array, its scalar/string fields (reference fields as an address), its string value if it is a string, and the first elements if it is an array. Get an address from heap_find. Requires a paused .NET/CoreCLR process; the dnSpy build's bitness must match the target. Reads memory directly (ClrMD passive), never suspending the target.",
				Schema.Object(
					("address", Schema.Str("Object address, hex (0x...) or decimal"), true)),
				HeapObject, readOnly: true);
		}

		string HeapStats(JObject args) {
			var top = Clamp((int?)args["top"] ?? 100, 1, MaxTopTypes);
			return RunHelper("stats", top.ToString(CultureInfo.InvariantCulture));
		}

		string HeapFind(JObject args) {
			var typeArg = (string?)args["type"] ?? throw new ArgumentException("'type' is required");
			var max = Clamp((int?)args["max"] ?? 200, 1, MaxInstances);
			// The helper handles the trailing-'*' prefix match; pass the type name through verbatim so the
			// 'type' it echoes back matches exactly what the caller asked for.
			return RunHelper("find", typeArg, max.ToString(CultureInfo.InvariantCulture));
		}

		string HeapObject(JObject args) {
			var addr = ParseAddress(args["address"]);
			// Normalise to 0x-hex so the helper parses it unambiguously.
			return RunHelper("object", "0x" + addr.ToString("X"));
		}

		// Snapshot + validate the current process, locate the matching-bitness helper, spawn it with the
		// command and args, and shape its single JSON object into the tool result. The paused/exists
		// pre-checks run first so their clear messages reach the caller verbatim; the helper reports its
		// own failures (torn-down process, no CLR, bad address, …) as {"error":"..."}, surfaced as-is.
		string RunHelper(string command, params string[] extraArgs) {
			var snap = Snapshot();
			if (!snap.Exists)
				throw new InvalidOperationException("no debugged process; start a session and pause it (hit a breakpoint or call dbg_break) before walking the heap");
			if (!snap.Paused)
				throw new InvalidOperationException("the process must be paused (hit a breakpoint or call dbg_break) before its managed heap can be walked");

			var helper = LocateHelper(snap.Bitness);

			var argv = new List<string>(2 + extraArgs.Length) {
				command,
				snap.Pid.ToString(CultureInfo.InvariantCulture),
			};
			argv.AddRange(extraArgs);

			var (exitCode, stdout, stderr) = Spawn(helper, argv);

			JObject result;
			try {
				result = JObject.Parse(stdout);
			}
			catch (Exception ex) {
				var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
				throw new InvalidOperationException($"the heap helper did not return valid JSON (exit {exitCode}): {Trim(detail)} [{ex.Message}]");
			}
			var err = (string?)result["error"];
			if (err is not null)
				throw new InvalidOperationException("heap helper: " + err);
			return Json(result);
		}

		// Find dnSpy.MCP.HeapHelper.exe for the target bitness next to this extension. The helper is
		// published to <extension dir>\HeapHelper\win-{x64|x86}\; probe that first, then one directory up
		// (covers the build.ps1 layout where the extension DLL and the HeapHelper folder move together)
		// and AppContext.BaseDirectory as a fallback.
		static string LocateHelper(int bitness) {
			var rid = bitness == 32 ? "win-x86" : "win-x64";
			const string exeName = "dnSpy.MCP.HeapHelper.exe";

			var bases = new List<string>();
			var loc = Assembly.GetExecutingAssembly().Location;
			if (!string.IsNullOrEmpty(loc)) {
				var dir = Path.GetDirectoryName(loc);
				if (!string.IsNullOrEmpty(dir)) {
					bases.Add(dir!);
					var parent = Path.GetDirectoryName(dir);
					if (!string.IsNullOrEmpty(parent))
						bases.Add(parent!);
				}
			}
			var baseDir = AppContext.BaseDirectory;
			if (!string.IsNullOrEmpty(baseDir))
				bases.Add(baseDir);

			foreach (var b in bases) {
				var candidate = Path.Combine(b, "HeapHelper", rid, exeName);
				if (File.Exists(candidate))
					return candidate;
			}

			var expected = bases.Count > 0
				? Path.Combine(bases[0], "HeapHelper", rid, exeName)
				: Path.Combine("HeapHelper", rid, exeName);
			throw new InvalidOperationException(
				$"the {rid} heap helper ({exeName}) was not found next to the extension (expected under HeapHelper\\{rid}). " +
				$"Rebuild the dnSpy.MCP extension so the helper is published, then retry. Expected path: {expected}");
		}

		// Run the helper with stdout/stderr redirected and no window, on the calling (request) thread.
		// stdout is read to completion; the process is killed if it overruns the timeout.
		(int exitCode, string stdout, string stderr) Spawn(string exe, List<string> argv) {
			var psi = new ProcessStartInfo {
				FileName = exe,
				Arguments = BuildArguments(argv),
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
				WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory,
				StandardOutputEncoding = Encoding.UTF8,
				StandardErrorEncoding = Encoding.UTF8,
			};

			using var proc = new Process { StartInfo = psi };
			try {
				proc.Start();
			}
			catch (Exception ex) {
				throw new InvalidOperationException($"could not start the heap helper ({exe}): {ex.Message}");
			}

			// Read both streams asynchronously before waiting, so a large payload cannot deadlock on a
			// full pipe buffer.
			var outTask = proc.StandardOutput.ReadToEndAsync();
			var errTask = proc.StandardError.ReadToEndAsync();

			if (!proc.WaitForExit(HelperTimeoutMs)) {
				try { proc.Kill(); } catch { /* already gone */ }
				throw new InvalidOperationException($"the heap helper did not finish within {HelperTimeoutMs / 1000} s and was terminated (the heap may be extremely large, or the target resumed)");
			}
			// The timed overload can return before the redirected streams have flushed; block for the rest.
			proc.WaitForExit();

			return (proc.ExitCode, SafeResult(outTask), SafeResult(errTask));
		}

		static string SafeResult(Task<string> task) {
			try {
				return task.GetAwaiter().GetResult() ?? string.Empty;
			}
			catch {
				return string.Empty;
			}
		}

		// Quote arguments per the Windows CommandLineToArgvW rules so a type name with spaces, quotes or
		// trailing backslashes round-trips. (net48 has no ProcessStartInfo.ArgumentList.)
		static string BuildArguments(List<string> argv) {
			var sb = new StringBuilder();
			foreach (var a in argv) {
				if (sb.Length > 0)
					sb.Append(' ');
				AppendArgument(sb, a);
			}
			return sb.ToString();
		}

		static void AppendArgument(StringBuilder sb, string arg) {
			if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) {
				sb.Append(arg);
				return;
			}
			sb.Append('"');
			for (int i = 0; ; i++) {
				var backslashes = 0;
				while (i < arg.Length && arg[i] == '\\') {
					i++;
					backslashes++;
				}
				if (i == arg.Length) {
					// Escape all trailing backslashes so the closing quote is not consumed.
					sb.Append('\\', backslashes * 2);
					break;
				}
				if (arg[i] == '"') {
					// Escape the backslashes preceding the quote, then the quote itself.
					sb.Append('\\', backslashes * 2 + 1);
					sb.Append('"');
				}
				else {
					sb.Append('\\', backslashes);
					sb.Append(arg[i]);
				}
			}
			sb.Append('"');
		}

		static string Trim(string s) {
			s = s.Trim();
			return s.Length <= 500 ? s : s.Substring(0, 500) + "…";
		}

		// The one debugger-object touch, marshalled onto the dispatcher: a trivial, instant snapshot of the
		// current process so the helper spawn that follows needs no further debugger access.
		ProcInfo Snapshot() => dbg.Invoke(() => {
			var processes = Mgr.Processes;
			if (processes.Length == 0)
				return new ProcInfo(0, 0, false, false);
			var p = Mgr.CurrentProcess.Current ?? processes[0];
			return new ProcInfo(p.Id, p.Bitness, p.State == DbgProcessState.Paused, true);
		});

		// Accept the address as a JSON number or a hex/decimal string.
		static ulong ParseAddress(JToken? tok) {
			if (tok is null || tok.Type == JTokenType.Null)
				throw new ArgumentException("'address' is required");
			if (tok.Type == JTokenType.Integer)
				return (ulong)tok;
			return ParseULong((string?)tok, "address");
		}

		readonly struct ProcInfo {
			public readonly int Pid;
			public readonly int Bitness;
			public readonly bool Paused;
			public readonly bool Exists;
			public ProcInfo(int pid, int bitness, bool paused, bool exists) {
				Pid = pid;
				Bitness = bitness;
				Paused = paused;
				Exists = exists;
			}
		}
	}
}
