using System;
using System.IO;
using System.Threading;
using dnSpy.MCP.Tests; // Rpc/RpcResponse, shared with the Tier 1 project via a linked source file
using Newtonsoft.Json.Linq;
using Xunit;

namespace dnSpy.MCP.IntegrationTests {
	/// <summary>
	/// A Fact that skips itself unless a live dnSpy MCP endpoint was configured, so the suite is
	/// harmless on a machine that is not set up for Tier 2.
	/// </summary>
	public sealed class DbgFactAttribute : FactAttribute {
		public DbgFactAttribute() {
			if (!Dbg.Configured)
				Skip = "DNSPY_MCP_TEST_URL is not set — run tests/run-integration.ps1";
		}
	}

	/// <summary>Talks to the running dnSpy: calls tools, waits for pauses, and resets state.</summary>
	static class Dbg {
		static int nextId = 1;

		public static string? Url => Environment.GetEnvironmentVariable("DNSPY_MCP_TEST_URL");
		public static string? Token => Environment.GetEnvironmentVariable("DNSPY_MCP_TEST_TOKEN");
		public static bool Configured => !string.IsNullOrEmpty(Url);

		/// <summary>Directory holding the built fixture debuggee for the given target framework.</summary>
		public static string FixtureDir(string tfm = "net8.0") {
			var root = Environment.GetEnvironmentVariable("DNSPY_MCP_TEST_FIXTURE")
				?? throw new InvalidOperationException("DNSPY_MCP_TEST_FIXTURE is not set");
			return Path.Combine(root, tfm);
		}

		public static string FixtureDll(string tfm = "net8.0") => Path.Combine(FixtureDir(tfm), "dbgtest.dll");
		public static string FixtureExe(string tfm = "net8.0") => Path.Combine(FixtureDir(tfm), "dbgtest.exe");

		/// <summary>Calls a tool and returns its text payload. Throws if the tool reported an error.</summary>
		public static string Call(string tool, JObject? args = null) {
			var res = Rpc.Post(Url!, Rpc.CallTool(Interlocked.Increment(ref nextId), tool, args), Token);
			if (res.Json["error"] is not null)
				throw new InvalidOperationException($"{tool}: protocol error {res.Json["error"]}");
			if (res.ToolIsError)
				throw new DbgToolException(tool, res.ToolText);
			return res.ToolText;
		}

		/// <summary>Calls a tool, returning its error text instead of throwing.</summary>
		public static string CallExpectingError(string tool, JObject? args = null) {
			try {
				var text = Call(tool, args);
				throw new InvalidOperationException($"{tool} unexpectedly succeeded: {text}");
			}
			catch (DbgToolException ex) {
				return ex.ToolMessage;
			}
		}

		public static JObject CallJson(string tool, JObject? args = null) => JObject.Parse(Call(tool, args));
		public static JArray CallArray(string tool, JObject? args = null) => JArray.Parse(Call(tool, args));

		public static JObject Status() => CallJson("dbg_status");

		/// <summary>
		/// Resolves a method name to its metadata token by asking the server, then clears the
		/// breakpoints that lookup created. Hard-coding a token would silently target whatever method
		/// happens to occupy that row after any edit to the fixture.
		/// </summary>
		public static string TokenOf(string method, string? module = null, int overloadIndex = 0) {
			module ??= FixtureDll();
			var bps = CallArray("bp_add_method", new JObject { ["module"] = module, ["method"] = method });
			var token = (string)bps[overloadIndex]["token"]!;
			Call("bp_remove", new JObject { ["all"] = true });
			return token;
		}

		/// <summary>Hit count reads as null until a session is running; treat that as zero.</summary>
		public static int HitCount(JToken breakpoint) => (int?)breakpoint["hitCount"] ?? 0;

		/// <summary>
		/// Reads a formatted value as a number. The extension asks for decimal, but assert on the
		/// numeric value rather than its spelling so these tests check the debugger, not the formatter.
		/// </summary>
		public static long Number(JToken? value) {
			var text = ((string?)value)?.Trim()
				?? throw new InvalidOperationException("value was null");
			return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
				? Convert.ToInt64(text.Substring(2), 16)
				: long.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
		}
		public static bool IsDebugging => (bool)Status()["isDebugging"]!;
		public static bool IsRunning => (bool)Status()["isRunning"]!;

		/// <summary>
		/// Drops all breakpoints and stops any session, so each test starts from a known state.
		///
		/// The teardown order matters. dnSpy's own Locals window re-evaluates whenever the call stack
		/// changes, and that path decompiles the paused method by reading the debuggee's PE image
		/// straight out of its memory. Terminating a process while it is paused lets that UI refresh
		/// race the teardown and read freed memory, which takes the whole app down with an
		/// AccessViolationException — no MCP code involved. Clearing breakpoints and resuming first
		/// means the UI is not sitting on a frame when the process goes away.
		/// </summary>
		public static void Reset() {
			TryCall("bp_remove", new JObject { ["all"] = true });
			TryCall("mbp_remove", new JObject { ["all"] = true });
			if (!IsDebugging)
				return;

			if (!IsRunning) {
				TryCall("dbg_continue");
				WaitUntil(() => IsRunning, 5000);
			}
			TryCall("dbg_stop");
			// Wait for the process list to empty, not just for isDebugging: a debuggee that outlives
			// the session keeps the engine busy and makes the next test's stop look like it hung.
			WaitUntil(() => !IsDebugging && ((JArray)Status()["processes"]!).Count == 0, 20000);
			// Let the UI drain the queued frames-changed work before the next test repopulates it.
			Thread.Sleep(750);
		}

		public static void TryCall(string tool, JObject? args = null) {
			try { Call(tool, args); }
			catch (Exception) { /* best-effort cleanup */ }
		}

		public static bool WaitUntil(Func<bool> condition, int timeoutMs) {
			var deadline = Environment.TickCount64 + timeoutMs;
			while (Environment.TickCount64 < deadline) {
				try {
					if (condition())
						return true;
				}
				catch (Exception) {
					// Transient while the engine is starting or tearing down.
				}
				Thread.Sleep(100);
			}
			return false;
		}

		/// <summary>Blocks until a process pauses, returning the paused-state payload.</summary>
		public static JObject WaitForBreak(int timeoutMs = 30000) {
			var text = Call("dbg_wait_for_break", new JObject { ["timeout_ms"] = timeoutMs });
			if (text.StartsWith("timeout", StringComparison.Ordinal))
				throw new TimeoutException("no process paused: " + text);
			return JObject.Parse(text);
		}
	}

	sealed class DbgToolException : Exception {
		public string ToolMessage { get; }
		public DbgToolException(string tool, string message) : base($"{tool}: {message}") => ToolMessage = message;
	}
}
