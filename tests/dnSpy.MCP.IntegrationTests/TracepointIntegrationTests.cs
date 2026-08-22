using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace dnSpy.MCP.IntegrationTests {
	/// <summary>
	/// bp_add_trace + trace_log: a tracepoint is a code breakpoint whose Trace is set with Continue=true,
	/// so when it is hit dnSpy logs a message and auto-resumes instead of pausing. These are Tier-2 debug
	/// tests — they set a tracepoint on the fixture's looped Tick method, let the process run through, and
	/// read the collected records back. Nothing here waits for a break: a tracepoint never stops, so
	/// dbg_wait_for_break would only time out; the tests poll trace_log until the hits land instead.
	/// </summary>
	[Collection("dnSpy")]
	public class TracepointIntegrationTests : IDisposable {
		public TracepointIntegrationTests() => Dbg.Reset();
		public void Dispose() => Dbg.Reset();

		// The trace collector lives in the dnSpy process and is NOT reset by Dbg.Reset() (which only
		// clears breakpoints and stops the session). Drain it at the start of each test so records from
		// an earlier test's session cannot bleed into this one's assertions.
		static void DrainTrace() => Dbg.Call("trace_log", new JObject { ["clear"] = true });

		// How many records trace_log currently returns (default max is large enough for the few hits here).
		static int TraceCount() => (int)Dbg.CallJson("trace_log")["count"]!;

		[DbgFact]
		public void A_tracepoint_logs_every_hit_and_never_pauses() {
			// RED before impl: bp_add_trace and trace_log don't exist yet, so the first call is an unknown
			// tool and this fails. GREEN proves the defining property of a tracepoint: Tick is called three
			// times from Warmup, so three records must appear WHILE the process keeps running — it is never
			// left paused, because Trace.Continue=true makes dnSpy log and resume rather than break.
			DrainTrace();
			Dbg.CallArray("bp_add_trace", new JObject {
				["module"] = Dbg.FixtureDll(),
				["method"] = "DbgTest.Program.Tick",
				["message"] = "tick",
			});
			// Deliberately NO normal breakpoint: nothing in this run should ever pause.
			Dbg.Call("dbg_start", new JObject { ["path"] = Dbg.FixtureDll() });

			// Tick runs during Warmup, right at startup; poll until the three hits are collected.
			Assert.True(Dbg.WaitUntil(() => TraceCount() >= 3, 20000),
				"the tracepoint should have logged at least 3 times (Tick is called 3x from Warmup)");

			// The whole point: a tracepoint logs and continues. The process must not be sitting paused.
			Assert.True(Dbg.IsDebugging && Dbg.IsRunning,
				"a tracepoint must auto-resume — the process should still be running, not paused");

			var res = Dbg.CallJson("trace_log", new JObject { ["max"] = 200 });
			var entries = (JArray)res["entries"]!;
			Assert.True((int)res["count"]! >= 3);
			Assert.True(entries.Count >= 3);

			// Every record carries the essentials the spec promises.
			foreach (var e in entries) {
				Assert.NotNull((string?)e["timestamp"]);
				Assert.NotNull((int?)e["breakpointId"]);
				Assert.NotNull((string?)e["message"]);
			}
			// Sequence numbers strictly increase in hit order.
			var seqs = entries.Select(e => (long)e["seq"]!).ToArray();
			for (int i = 1; i < seqs.Length; i++)
				Assert.True(seqs[i] > seqs[i - 1], "seq must be monotonically increasing");
		}

		[DbgFact]
		public void A_tracepoint_message_interpolates_expressions_against_the_hit_frame() {
			// RED before impl: the tools don't exist. GREEN additionally requires eval-at-trace-time to
			// work: {i} is Tick's parameter, in scope at method entry, so Tick(0),Tick(1),Tick(2) must
			// render "tick 0","tick 1","tick 2". If the event handler could not evaluate, these would come
			// back as the raw template "tick {i}" (or "tick {!err}") and this test would fail loudly.
			DrainTrace();
			Dbg.CallArray("bp_add_trace", new JObject {
				["module"] = Dbg.FixtureDll(),
				["method"] = "DbgTest.Program.Tick",
				["message"] = "tick {i}",
			});
			Dbg.Call("dbg_start", new JObject { ["path"] = Dbg.FixtureDll() });
			Assert.True(Dbg.WaitUntil(() => TraceCount() >= 3, 20000), "expected 3 tracepoint hits");

			var messages = ((JArray)Dbg.CallJson("trace_log")["entries"]!)
				.Select(e => (string?)e["message"]).ToList();
			Assert.Contains("tick 0", messages);
			Assert.Contains("tick 1", messages);
			Assert.Contains("tick 2", messages);
		}

		[DbgFact]
		public void Trace_log_clear_empties_the_buffer() {
			// RED before impl: no trace_log. GREEN: clear returns what it drained, then the buffer is empty.
			// Tick only runs 3x (in Warmup), so after the drain no further records can arrive and a second
			// read must report zero — which is what proves the clear actually emptied the buffer.
			DrainTrace();
			Dbg.CallArray("bp_add_trace", new JObject {
				["module"] = Dbg.FixtureDll(),
				["method"] = "DbgTest.Program.Tick",
				["message"] = "tick",
			});
			Dbg.Call("dbg_start", new JObject { ["path"] = Dbg.FixtureDll() });
			Assert.True(Dbg.WaitUntil(() => TraceCount() >= 3, 20000), "expected tracepoint hits before clearing");

			var cleared = Dbg.CallJson("trace_log", new JObject { ["clear"] = true });
			Assert.True((int)cleared["count"]! >= 3, "the clearing read should still return what it drained");

			var after = Dbg.CallJson("trace_log");
			Assert.Equal(0, (int)after["count"]!);
			Assert.Empty((JArray)after["entries"]!);
		}

		[DbgFact]
		public void Bp_add_trace_requires_a_message() {
			// RED before impl: unknown tool. GREEN: a tracepoint with no message is meaningless, so the
			// tool rejects it before touching the debugger. No session needed — this is pure validation.
			var error = Dbg.CallExpectingError("bp_add_trace", new JObject {
				["module"] = Dbg.FixtureDll(),
				["method"] = "DbgTest.Program.Tick",
			});
			Assert.Contains("message", error, StringComparison.OrdinalIgnoreCase);
		}

		[DbgFact]
		public void Bp_add_trace_rejects_an_unknown_method() {
			// RED before impl: unknown tool. GREEN: an unresolvable method name fails cleanly with the same
			// "method not found" MetadataResolver reports for the other breakpoint tools — not a crash.
			var error = Dbg.CallExpectingError("bp_add_trace", new JObject {
				["module"] = Dbg.FixtureDll(),
				["method"] = "DbgTest.Program.NoSuchMethod",
				["message"] = "hi",
			});
			Assert.Contains("not found", error, StringComparison.OrdinalIgnoreCase);
		}
	}
}
