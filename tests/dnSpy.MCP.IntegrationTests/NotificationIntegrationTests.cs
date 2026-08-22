using System;
using System.Diagnostics;
using System.Net;
using dnSpy.MCP.Tests; // SseStream, shared with the Tier 1 project via a linked source file
using Newtonsoft.Json.Linq;
using Xunit;

namespace dnSpy.MCP.IntegrationTests {
	/// <summary>
	/// The SSE push path driven by the debugger itself. Tier 1 can only broadcast a synthetic frame
	/// into the server, which proves the plumbing but says nothing about whether a real pause ever
	/// reaches it: the subscription to DbgManager.ProcessPaused, the pid and thread it reports, and
	/// the fact that it keeps firing after the first pause all live outside that test.
	/// </summary>
	[Collection("dnSpy")]
	public class NotificationIntegrationTests : IDisposable {
		/// <summary>
		/// Every wait for a frame is bounded by this. A missing notification is a defect, and it has
		/// to be reported as one — a test that waited forever would hang the suite instead.
		/// </summary>
		const int FrameTimeoutMs = 20000;

		public NotificationIntegrationTests() => Dbg.Reset();
		public void Dispose() => Dbg.Reset();

		[DbgFact]
		public void A_breakpoint_hit_pushes_a_paused_notification_for_the_real_process_and_thread() {
			// Opened before the session starts: Broadcast returns early when no client is connected,
			// so a stream opened after the pause would have nothing to receive.
			using var sse = new SseStream(Dbg.Url!, Dbg.Token);
			Assert.Equal(HttpStatusCode.OK, sse.Status);

			var paused = PauseInsideAdd();

			var frame = sse.WaitForFrame(IsPausedFrame, FrameTimeoutMs);
			Assert.True(frame is not null, $"no notifications/paused frame arrived within {FrameTimeoutMs}ms");

			var message = Payload(frame!);
			Assert.Equal("2.0", (string?)message["jsonrpc"]);
			Assert.Equal("notifications/paused", (string?)message["method"]);

			// The point of the test: these numbers have to be the debugger's own, not the plausible
			// constants a synthetic broadcast would carry. Cross-check both against the tools.
			var pid = (int?)message["params"]!["pid"];
			var threadId = (long?)message["params"]!["threadId"];
			Assert.Contains((JArray)Dbg.Status()["processes"]!, p => (int?)p["id"] == pid);
			Assert.Contains(Dbg.CallArray("dbg_threads"), t => (long?)t["id"] == threadId);
			Assert.Equal((long?)paused["thread"]!["id"], threadId);
		}

		[DbgFact]
		public void A_second_pause_pushes_a_second_notification() {
			using var sse = new SseStream(Dbg.Url!, Dbg.Token);
			Assert.Equal(HttpStatusCode.OK, sse.Status);

			var mainThread = (long)PauseInsideAdd()["thread"]!["id"]!;

			// The two pauses are told apart by thread id because it is the only field that can differ
			// between two frames from one process, and WaitForFrame returns the first match — two
			// byte-identical frames would be indistinguishable however long the test waited. Only the
			// fixture's worker thread ever calls WorkerTick, so breaking there guarantees a different
			// id. TokenOf also clears the Add breakpoint, which is what keeps the resume below from
			// stopping on the main thread again.
			var workerTickToken = Dbg.TokenOf("DbgTest.Program.WorkerTick");
			Dbg.Call("bp_add", new JObject {
				["module"] = Dbg.FixtureDll(),
				["token"] = workerTickToken,
			});

			Dbg.Call("dbg_continue");
			// dbg_wait_for_break reports as soon as it sees a stopped engine, so without this it could
			// answer with the pause we just left and the "second" notification would never be real.
			Assert.True(Dbg.WaitUntil(() => Dbg.IsRunning, 15000), "the process never resumed");

			var workerThread = (long)Dbg.WaitForBreak()["thread"]!["id"]!;
			Assert.NotEqual(mainThread, workerThread);

			Assert.True(sse.WaitForFrame(f => IsPausedFrame(f) && HasThreadId(f, mainThread), FrameTimeoutMs) is not null,
				$"no notification for the first pause (thread {mainThread})");
			Assert.True(sse.WaitForFrame(f => IsPausedFrame(f) && HasThreadId(f, workerThread), FrameTimeoutMs) is not null,
				$"no notification for the second pause (thread {workerThread})");
		}

		// The stream is a second door into the same server: an agent that could subscribe to pauses
		// without the bearer token would learn what is being debugged, and every pause thereafter.
		[DbgFact]
		public void Opening_the_stream_needs_the_same_bearer_token_as_every_tool_call() {
			Assert.False(string.IsNullOrEmpty(Dbg.Token),
				"DNSPY_MCP_TEST_TOKEN is not set, so this test cannot tell an accepted token from a missing one");

			using var anonymous = new SseStream(Dbg.Url!);
			Assert.Equal(HttpStatusCode.Unauthorized, anonymous.Status);

			using var wrongToken = new SseStream(Dbg.Url!, Dbg.Token + "x");
			Assert.Equal(HttpStatusCode.Unauthorized, wrongToken.Status);

			using var authorized = new SseStream(Dbg.Url!, Dbg.Token);
			Assert.Equal(HttpStatusCode.OK, authorized.Status);
		}

		// Guards the tests above as much as the server: they only mean something if a frame that
		// never arrives comes back as a failure rather than blocking the run forever.
		[DbgFact]
		public void Waiting_for_a_notification_that_never_arrives_ends_as_a_timeout() {
			using var sse = new SseStream(Dbg.Url!, Dbg.Token);
			Assert.Equal(HttpStatusCode.OK, sse.Status);

			// Reset left nothing being debugged, so no pause can happen and no frame can arrive.
			var sw = Stopwatch.StartNew();
			var frame = sse.WaitForFrame(IsPausedFrame, 2000);
			sw.Stop();

			Assert.Null(frame);
			Assert.True(sw.Elapsed.TotalSeconds < 15,
				$"the wait returned only after {sw.Elapsed.TotalSeconds:F1}s, so its 2s bound is not being honoured");
		}

		/// <summary>Runs the fixture until it stops inside Program.Add, returning the pause payload.</summary>
		static JObject PauseInsideAdd() {
			Dbg.Call("bp_add", new JObject {
				["module"] = Dbg.FixtureDll(),
				["token"] = Dbg.TokenOf("DbgTest.Program.Add"),
			});
			Dbg.Call("dbg_start", new JObject { ["path"] = Dbg.FixtureDll() });
			return Dbg.WaitForBreak();
		}

		static bool IsPausedFrame(string frame) => frame.Contains("notifications/paused");

		/// <summary>Only ever applied to a frame already known to be a notification, so it can parse.</summary>
		static bool HasThreadId(string frame, long threadId) =>
			(long?)Payload(frame)["params"]?["threadId"] == threadId;

		/// <summary>The JSON of an SSE frame, whose wire form is "event: message\ndata: {...}".</summary>
		static JObject Payload(string frame) {
			const string marker = "data: ";
			var index = frame.IndexOf(marker, StringComparison.Ordinal);
			if (index < 0)
				throw new InvalidOperationException("SSE frame carried no data line: " + frame);
			return JObject.Parse(frame.Substring(index + marker.Length));
		}
	}
}
