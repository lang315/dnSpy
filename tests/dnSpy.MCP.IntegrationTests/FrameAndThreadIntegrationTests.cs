using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace dnSpy.MCP.IntegrationTests {
	/// <summary>
	/// The frame_index and thread_id arguments carried by the inspection tools. Every other test reads
	/// frame 0 of whichever thread happens to be current, so a tool that silently ignored either
	/// argument would still pass them all while answering the wrong question for an agent walking a
	/// stack or comparing threads.
	/// </summary>
	[Collection("dnSpy")]
	public class FrameAndThreadIntegrationTests : IDisposable {
		/// <summary>Set by the fixture on its background thread; the main thread never carries it.</summary>
		const string WorkerThreadName = "dbgtest-worker";

		public FrameAndThreadIntegrationTests() {
			Dbg.Reset();
			PauseInsideLevel3();
		}

		public void Dispose() => Dbg.Reset();

		/// <summary>
		/// Stops the fixture at the top of Level3 with the whole chain live above it. three == 16 only
		/// happens on the seed == 7 iteration, which fixes the callers: Level2 has two == 8 and
		/// mid == 16, Level1 has seed == 7 and one == 8.
		/// </summary>
		static void PauseInsideLevel3() {
			Dbg.Call("bp_add", new JObject {
				["module"] = Dbg.FixtureDll(),
				["token"] = Dbg.TokenOf("DbgTest.Program.Level3"),
				["condition"] = "three == 16",
			});
			Dbg.Call("dbg_start", new JObject { ["path"] = Dbg.FixtureDll() });
			Dbg.WaitForBreak();
		}

		[DbgFact]
		public void Locals_are_read_from_the_requested_caller_frame() {
			// Guard: the locals below only mean anything if these are the frames they are read from.
			var frames = (JArray)Dbg.CallJson("dbg_callstack")["frames"]!;
			Assert.Contains("Level3", (string?)frames[0]["frame"]);
			Assert.Contains("Level2", (string?)frames[1]["frame"]);
			Assert.Contains("Level1", (string?)frames[2]["frame"]);

			var level2 = Dbg.CallArray("dbg_locals", new JObject { ["frame_index"] = 1 });
			Assert.Equal(8, Dbg.Number(level2.First(l => (string?)l["name"] == "two")["value"]));
			Assert.Equal(16, Dbg.Number(level2.First(l => (string?)l["name"] == "mid")["value"]));

			var level1 = Dbg.CallArray("dbg_locals", new JObject { ["frame_index"] = 2 });
			Assert.Equal(7, Dbg.Number(level1.First(l => (string?)l["name"] == "seed")["value"]));
			Assert.Equal(8, Dbg.Number(level1.First(l => (string?)l["name"] == "one")["value"]));
		}

		[DbgFact]
		public void An_expression_is_evaluated_against_the_requested_frames_locals() {
			var res = Dbg.CallJson("dbg_eval", new JObject {
				["expression"] = "seed + one",
				["frame_index"] = 2,
			});
			Assert.Equal(15, Dbg.Number(res["value"]));

			// Level3's own frame has neither name, so an evaluation that quietly fell back to frame 0
			// would fail here instead of answering 15 above.
			var error = Dbg.CallExpectingError("dbg_eval", new JObject { ["expression"] = "seed + one" });
			Assert.False(string.IsNullOrWhiteSpace(error));
		}

		[DbgFact]
		public void A_frame_index_past_the_top_of_the_stack_is_an_error() {
			var error = Dbg.CallExpectingError("dbg_locals", new JObject { ["frame_index"] = 5000 });

			Assert.Contains("5000", error);
			Assert.Contains("out of range", error, StringComparison.OrdinalIgnoreCase);
		}

		[DbgFact]
		public void The_thread_list_names_the_fixtures_worker_thread() {
			var threads = Dbg.CallArray("dbg_threads");

			Assert.Contains(threads, t => (string?)t["name"] == WorkerThreadName);
		}

		// WorkerTick has exactly one caller, on the background thread, so this pins a thread that is
		// not the one every other test stops on — the only way to tell a real thread_id lookup from a
		// tool that always answers about the current thread.
		[DbgFact]
		public void A_breakpoint_only_the_worker_reaches_pauses_the_worker_thread() {
			var paused = BreakOnWorkerTick();
			Assert.Equal(WorkerThreadName, (string?)paused["thread"]!["name"]);

			var threadId = (long)paused["thread"]!["id"]!;
			var stack = Dbg.CallJson("dbg_callstack", new JObject { ["thread_id"] = threadId });

			Assert.Equal(threadId, (long)stack["threadId"]!);
			Assert.Contains("WorkerTick", (string?)((JArray)stack["frames"]!)[0]["frame"]);
		}

		[DbgFact]
		public void Setting_the_current_thread_redirects_the_calls_that_omit_one() {
			var main = (long)Dbg.CallJson("dbg_callstack")["threadId"]!;
			var worker = (long)Dbg.CallArray("dbg_threads")
				.First(t => (string?)t["name"] == WorkerThreadName)["id"]!;
			Assert.NotEqual(main, worker);

			Dbg.Call("dbg_set_thread", new JObject { ["thread_id"] = worker });

			// Same call as the first line of this test, no thread_id either time.
			Assert.Equal(worker, (long)Dbg.CallJson("dbg_callstack")["threadId"]!);
		}

		[DbgFact]
		public void The_call_stack_returns_no_more_frames_than_asked_for() {
			var all = (JArray)Dbg.CallJson("dbg_callstack")["frames"]!;
			// Level3, Level2, Level1 and Main at least, so the cap below has something to cut.
			Assert.True(all.Count > 2, $"expected the Level3 stack to be deeper than 2 frames, got {all.Count}");

			var capped = (JArray)Dbg.CallJson("dbg_callstack", new JObject { ["max_frames"] = 2 })["frames"]!;

			Assert.Equal(2, capped.Count);
			// The cap must drop the oldest frames, not the innermost ones.
			Assert.Contains("Level3", (string?)capped[0]["frame"]);
		}

		/// <summary>
		/// Drops the Level3 breakpoint, arms WorkerTick instead, and runs on to the worker's next tick
		/// (every 100 ms).
		/// </summary>
		static JObject BreakOnWorkerTick() {
			// TokenOf clears the breakpoints its own lookup creates, and with them the Level3 one, so
			// the only armed breakpoint afterwards is WorkerTick's.
			var token = Dbg.TokenOf("DbgTest.Program.WorkerTick");
			Dbg.Call("bp_add", new JObject { ["module"] = Dbg.FixtureDll(), ["token"] = token });

			Dbg.Call("dbg_continue");
			// Wait for the resume before waiting for the pause: dbg_wait_for_break polls for "not
			// running", and the pause we are leaving would satisfy it immediately.
			Assert.True(Dbg.WaitUntil(() => Dbg.IsRunning, 10000), "the process never resumed");
			return Dbg.WaitForBreak();
		}
	}
}
