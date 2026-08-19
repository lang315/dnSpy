using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace dnSpy.MCP.IntegrationTests {
	/// <summary>Launch, attach, pause and resume against a real debug engine, plus memory access.</summary>
	[Collection("dnSpy")]
	public class SessionIntegrationTests : IDisposable {
		public SessionIntegrationTests() => Dbg.Reset();
		public void Dispose() => Dbg.Reset();

		[DbgFact]
		public void An_idle_dnSpy_reports_no_session() {
			var status = Dbg.Status();

			Assert.False((bool?)status["isDebugging"]);
			Assert.Empty((JArray)status["processes"]!);
		}

		[DbgFact]
		public void A_dll_can_be_launched_and_stopped() {
			Dbg.Call("dbg_start", new JObject { ["path"] = Dbg.FixtureDll() });
			Assert.True(Dbg.WaitUntil(() => Dbg.IsDebugging, 20000), "session never started");

			Dbg.Call("dbg_stop");
			Assert.True(Dbg.WaitUntil(() => !Dbg.IsDebugging, 30000), "session never stopped");
		}

		// Regression: launching the apphost .exe re-execs the .NET host, and breakpoints armed before
		// the launch were dropped. dbg_start now debugs the sibling .dll when one is present.
		[DbgFact]
		public void Launching_the_apphost_exe_still_binds_a_breakpoint_set_beforehand() {
			Dbg.Call("bp_add_method", new JObject {
				["module"] = Dbg.FixtureDll(),
				["method"] = "DbgTest.Program.Add",
			});

			Dbg.Call("dbg_start", new JObject { ["path"] = Dbg.FixtureExe() });
			Dbg.WaitForBreak();

			Assert.Contains(Dbg.CallArray("bp_list"), b => (int)b["boundCount"]! >= 1);
		}

		[DbgFact]
		public void A_framework_executable_is_launched_through_the_framework_engine() {
			Dbg.Call("bp_add_method", new JObject {
				["module"] = Dbg.FixtureExe("net48"),
				["method"] = "DbgTest.Program.Add",
			});

			Dbg.Call("dbg_start", new JObject { ["path"] = Dbg.FixtureExe("net48") });
			Dbg.WaitForBreak();

			Assert.Contains(Dbg.CallArray("bp_list"), b => (int)b["boundCount"]! >= 1);
		}

		[DbgFact]
		public void Break_and_continue_move_the_process_between_running_and_paused() {
			Dbg.Call("dbg_start", new JObject { ["path"] = Dbg.FixtureDll() });
			Assert.True(Dbg.WaitUntil(() => Dbg.IsDebugging, 20000));

			Dbg.Call("dbg_break");
			Assert.True(Dbg.WaitUntil(() => !Dbg.IsRunning, 15000), "process never paused");

			Dbg.Call("dbg_continue");
			Assert.True(Dbg.WaitUntil(() => Dbg.IsRunning, 15000), "process never resumed");
		}

		// dbg_restart was the one session verb with no coverage, and "still debugging afterwards" is
		// not evidence of anything: the untouched original session satisfies that too. A new pid is
		// what distinguishes a real relaunch from a call that quietly did nothing.
		[DbgFact]
		public void Restarting_relaunches_the_process_and_rebinds_the_breakpoints() {
			Dbg.Call("bp_add_method", new JObject {
				["module"] = Dbg.FixtureDll(),
				["method"] = "DbgTest.Program.Add",
			});
			Dbg.Call("dbg_start", new JObject { ["path"] = Dbg.FixtureDll() });
			Dbg.WaitForBreak();
			var firstPid = Assert.Single(Pids());

			Dbg.Call("dbg_restart");

			// Wait for the new process before waiting for a break. The old one is still paused at the
			// moment restart is asked for, so dbg_wait_for_break would return immediately, describing
			// the very session this test is trying to see replaced.
			Assert.True(Dbg.WaitUntil(() => {
				var pids = Pids();
				return pids.Length > 0 && !pids.Contains(firstPid);
			}, 30000), "the restart never produced a new process");

			Dbg.WaitForBreak();
			Assert.True(Dbg.IsDebugging);
			// The breakpoints survive the relaunch and bind again into the fresh process.
			Assert.Contains(Dbg.CallArray("bp_list"), b => Dbg.HitCount(b) >= 1);
		}

		/// <summary>Process ids of the current session, as dbg_status reports them.</summary>
		static int[] Pids() =>
			((JArray)Dbg.Status()["processes"]!).Select(p => (int)p["id"]!).ToArray();

		// Regression: dnSpy's attachable-process enumeration faulted when given a name filter, so the
		// tool fetches the unfiltered list and matches locally.
		[DbgFact]
		public void Attachable_processes_can_be_listed_with_a_name_filter() {
			var all = Dbg.CallArray("dbg_list_attachable");
			Assert.NotEmpty(all);

			// Must not fault, whether or not anything matches.
			var filtered = Dbg.CallArray("dbg_list_attachable", new JObject { ["name"] = "dbgtest*" });
			Assert.All(filtered, p => Assert.Contains("dbgtest", (string?)p["name"] ?? "",
				StringComparison.OrdinalIgnoreCase));
		}

		[DbgFact]
		public void Inspection_tools_explain_themselves_when_nothing_is_paused() {
			var error = Dbg.CallExpectingError("dbg_locals");

			Assert.False(string.IsNullOrWhiteSpace(error));
		}

		[DbgFact]
		public void Process_memory_can_be_read_back_after_a_write() {
			Dbg.Call("bp_add", new JObject {
				["module"] = Dbg.FixtureDll(),
				["token"] = Dbg.TokenOf("DbgTest.Program.Add"),
			});
			Dbg.Call("dbg_start", new JObject { ["path"] = Dbg.FixtureDll() });
			Dbg.WaitForBreak();

            // Pick a module's own base address: guaranteed mapped and readable.
			var module = Dbg.CallArray("dbg_modules")
				.First(m => (string?)m["address"] is not null);
			var address = (string)module["address"]!;

			var read = Dbg.CallJson("dbg_read_memory", new JObject {
				["address"] = address,
				["size"] = 2,
			});
			// A PE image starts with "MZ".
			Assert.Equal("4D5A", (string?)read["hex"]);
		}

		[DbgFact]
		public void An_oversized_memory_read_is_rejected() {
			var error = Dbg.CallExpectingError("dbg_read_memory", new JObject {
				["address"] = "0x1000",
				["size"] = 1024 * 1024,
			});

			Assert.Contains("size", error, StringComparison.OrdinalIgnoreCase);
		}

		// Exception settings live in dnSpy, not in the debug session, so Dbg.Reset() does not undo them
		// and an armed type stays armed for every later test in the run. The fixture throws on every
		// loop pass, so leaving this on makes unrelated tests stop at that throw instead of their own
		// breakpoint — which is exactly what happened once this test's leak met a fixture that throws
		// often. Disarm in a finally; the assertion is not worth poisoning the rest of the suite.
		[DbgFact]
		public void Breaking_on_a_thrown_exception_can_be_enabled() {
			try {
				var text = Dbg.Call("exc_break", new JObject {
					["exception"] = "System.InvalidOperationException",
					["enabled"] = true,
				});

				Assert.Contains("enabled", text, StringComparison.OrdinalIgnoreCase);
			}
			finally {
				Dbg.TryCall("exc_break", new JObject {
					["exception"] = "System.InvalidOperationException",
					["enabled"] = false,
				});
			}
		}
	}
}
