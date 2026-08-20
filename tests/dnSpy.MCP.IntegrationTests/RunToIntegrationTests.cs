using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace dnSpy.MCP.IntegrationTests {
	/// <summary>
	/// dbg_run_to: resume an active session and pause at a target method. Needs a live process, so this is
	/// a Tier-2 debug test — it starts the fixture, breaks at entry, then runs to a method deeper in.
	/// </summary>
	[Collection("dnSpy")]
	public class RunToIntegrationTests : IDisposable {
		public RunToIntegrationTests() => Dbg.Reset();
		public void Dispose() => Dbg.Reset();

		[DbgFact]
		public void Run_to_pauses_at_the_target_method() {
			// Start paused at the entry point, then run to Level3 (reached on the first loop iteration).
			Dbg.Call("dbg_start", new JObject { ["path"] = Dbg.FixtureDll(), ["break_at_entry"] = true });
			Dbg.WaitForBreak();

			var res = Dbg.CallJson("dbg_run_to", new JObject {
				["module"] = Dbg.FixtureDll(),
				["method"] = "DbgTest.Program.Level3",
				["timeout_ms"] = 30000,
			});
			Assert.True((bool)res["reached"]!, "run_to should have reached Level3");
			Assert.True((bool)res["isPaused"]!);
			Assert.True((long)res["totalHits"]! >= 1);

			// We are paused inside Level3.
			Dbg.WaitForBreak(); // ensure the stack is inspectable
			var frames = (JArray)Dbg.CallJson("dbg_callstack")["frames"]!;
			Assert.Contains(frames, f => ((string?)f["frame"])?.Contains("Level3") == true);
		}

		[DbgFact]
		public void Run_to_requires_a_target() {
			Dbg.Call("dbg_start", new JObject { ["path"] = Dbg.FixtureDll(), ["break_at_entry"] = true });
			Dbg.WaitForBreak();
			var error = Dbg.CallExpectingError("dbg_run_to", new JObject { ["module"] = Dbg.FixtureDll() });
			Assert.Contains("method", error, StringComparison.OrdinalIgnoreCase);
		}

		[DbgFact]
		public void Run_to_without_a_session_is_reported() {
			var error = Dbg.CallExpectingError("dbg_run_to", new JObject {
				["module"] = Dbg.FixtureDll(),
				["method"] = "DbgTest.Program.Level3",
			});
			Assert.Contains("debugging", error, StringComparison.OrdinalIgnoreCase);
		}
	}
}
