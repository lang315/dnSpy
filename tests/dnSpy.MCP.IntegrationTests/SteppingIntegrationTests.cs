using System;
using System.Diagnostics;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace dnSpy.MCP.IntegrationTests {
	/// <summary>
	/// Stepping against a real engine. dbg_step is the one tool that has to wait for the debuggee to
	/// move and then report where it landed, so "into entered the callee" and "over did not" can only
	/// be told apart by watching a live stack change.
	/// </summary>
	[Collection("dnSpy")]
	public class SteppingIntegrationTests : IDisposable {
		// Level1 is `{`, `var one = seed + 1;`, `return Level2(one);`, `}`. The exact number of
		// sequence points a Debug build emits before the call is a compiler detail, so the tests below
		// spend a budget of steps rather than assume a count; four is past the call either way.
		const int StepsPastTheCall = 4;

		public SteppingIntegrationTests() {
			Dbg.Reset();
			PauseInsideLevel1();
		}

		public void Dispose() => Dbg.Reset();

		/// <summary>
		/// Stops the fixture at the top of Level1 on the one iteration where seed == 7, which fixes
		/// every value down the chain: one == 8, then two == 8/mid == 16, then three == 16.
		/// </summary>
		static void PauseInsideLevel1() {
			Dbg.Call("bp_add", new JObject {
				["module"] = Dbg.FixtureDll(),
				["token"] = Dbg.TokenOf("DbgTest.Program.Level1"),
				["condition"] = "seed == 7",
			});
			Dbg.Call("dbg_start", new JObject { ["path"] = Dbg.FixtureDll() });
			Dbg.WaitForBreak();
		}

		[DbgFact]
		public void Stepping_into_a_call_enters_the_callee() {
			var frame = StepUntilTheTopFrameChanges("into");

			Assert.Contains("Level2", frame);
		}

		[DbgFact]
		public void Stepping_over_a_call_stays_in_the_caller() {
			var before = TopFrameOffset();

			Dbg.Call("dbg_step", new JObject { ["kind"] = "over" });

			// One step cannot leave Level1 — there is at least the assignment and the call ahead —
			// so this is the "advances but stays put" half of the claim.
			Assert.Contains("Level1", TopFrameName());
			Assert.NotEqual(before, TopFrameOffset());

			// The remaining budget certainly executes `return Level2(one)`. Stepping over it must
			// never surface a frame inside the chain it called.
			for (int i = 1; i < StepsPastTheCall; i++) {
				Dbg.Call("dbg_step", new JObject { ["kind"] = "over" });

				var frame = TopFrameName();
				Assert.DoesNotContain("Level2", frame);
				Assert.DoesNotContain("Level3", frame);
			}
		}

		[DbgFact]
		public void Stepping_out_returns_to_the_calling_frame() {
			ContinueToLevel3();
			Assert.Contains("Level3", TopFrameName());

			Dbg.Call("dbg_step", new JObject { ["kind"] = "out" });

			// Level2 is `return Level3(mid);`, so returning from Level3 lands back inside Level2
			// rather than skipping straight out to Level1.
			Assert.Contains("Level2", TopFrameName());
		}

		[DbgFact]
		public void Several_steps_in_a_row_stay_on_one_thread_with_a_coherent_stack() {
			var thread = (long)Dbg.CallJson("dbg_callstack")["threadId"]!;

			for (int i = 0; i < 6; i++) {
				Dbg.Call("dbg_step", new JObject { ["kind"] = "into" });

				var stack = Dbg.CallJson("dbg_callstack");
				Assert.Equal(thread, (long)stack["threadId"]!);

				var frames = (JArray)stack["frames"]!;
				Assert.NotEmpty(frames);
				// Indices must stay dense and top-down; a stack rebuilt wrongly after a step shows up
				// here before it shows up as a wrong local.
				for (int f = 0; f < frames.Count; f++)
					Assert.Equal(f, (int)frames[f]["index"]!);
				// Everything the fixture steps through is reached from Main, so Main must stay below.
				Assert.Contains("Main", string.Join(" ", frames.Select(f => (string?)f["frame"])));
			}
		}

		// A step is asynchronous: the tool arms a stepper and waits for its completion callback. If the
		// wait expires the tool must say the step is still outstanding, because the alternative — a
		// top-frame summary read while the debuggee is mid-step — reads as a completed step that landed
		// somewhere it never landed.
		// Measured: a step over in this fixture lands in well under a millisecond, so a 1 ms budget
		// usually wins the race rather than losing it. Both outcomes are correct and which one happens
		// is not the tool's contract. What is: the call returns when its own timeout expires, and it
		// never answers with something that is neither a landed step nor an admission that the step is
		// still outstanding — that third case is the one that would read as a step that landed
		// somewhere it never landed.
		[DbgFact]
		public void A_step_honours_its_own_timeout_whichever_way_the_race_goes() {
			var sw = Stopwatch.StartNew();
			var text = Dbg.Call("dbg_step", new JObject { ["kind"] = "over", ["timeout_ms"] = 1 });
			sw.Stop();

			var admittedOutstanding = text.Contains("did not complete", StringComparison.Ordinal);
			var landed = text.Contains("\"paused\"", StringComparison.Ordinal);
			Assert.True(admittedOutstanding || landed,
				"dbg_step answered with neither a landed step nor a timeout: " + text);
			Assert.True(sw.Elapsed.TotalSeconds < 10,
				"the call must return when its own timeout expires, not block on the engine");
		}

		/// <summary>
		/// Steps until the paused method changes, returning the new frame. A step on a line with no
		/// call behaves like a step over whichever kind is asked for, so counting steps to the call
		/// site would encode a compiler's sequence points into the test.
		/// </summary>
		static string StepUntilTheTopFrameChanges(string kind) {
			var start = TopFrameName();
			for (int i = 0; i < StepsPastTheCall; i++) {
				Dbg.Call("dbg_step", new JObject { ["kind"] = kind });
				var frame = TopFrameName();
				if (frame != start)
					return frame;
			}
			throw new InvalidOperationException(
				$"still in {start} after {StepsPastTheCall} '{kind}' steps");
		}

		/// <summary>
		/// Runs on from Level1 to Level3 of the same call. three == 16 happens only when seed == 7, so
		/// the stop is on this chain and not a later iteration's.
		/// </summary>
		static void ContinueToLevel3() {
			// TokenOf clears the breakpoints its lookup creates, which drops the Level1 breakpoint too
			// — wanted here, so the only armed breakpoint is the one being added.
			var token = Dbg.TokenOf("DbgTest.Program.Level3");
			Dbg.Call("bp_add", new JObject {
				["module"] = Dbg.FixtureDll(),
				["token"] = token,
				["condition"] = "three == 16",
			});

			Dbg.Call("dbg_continue");
			// Wait for the resume before waiting for the pause: dbg_wait_for_break polls for "not
			// running", and the current pause would satisfy it before the process has moved at all.
			Assert.True(Dbg.WaitUntil(() => Dbg.IsRunning, 10000), "the process never resumed");
			Dbg.WaitForBreak();
		}

		static JArray TopFrames() => (JArray)Dbg.CallJson("dbg_callstack")["frames"]!;
		static string TopFrameName() => (string?)TopFrames()[0]["frame"] ?? "";
		static string TopFrameOffset() => (string?)TopFrames()[0]["offset"] ?? "";
	}
}
