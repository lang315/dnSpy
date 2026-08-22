using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace dnSpy.MCP.Tests {
	/// <summary>
	/// Several MCP tools are long-polls by design: dbg_wait_for_break blocks until a process pauses,
	/// dbg_step until the step lands. An agent can have a number of them outstanding, so a blocked
	/// request must never cost the server its ability to answer anything else.
	/// </summary>
	public class ConcurrencyTests {
		const int LongPollers = 40;

		[Fact]
		public void A_crowd_of_blocked_long_polls_does_not_stall_a_quick_request() {
			using var srv = new McpTestServer();

			// Dedicated threads, not Task.Run: Rpc.Post blocks, so scheduling these on the ThreadPool
			// would starve the *client* and the server would never see all of them arrive.
			for (int i = 0; i < LongPollers; i++) {
				new Thread(() => {
					try {
						Rpc.Post(srv.McpUrl, Rpc.CallTool(1, "stub_slow", new JObject { ["ms"] = 30000 }));
					}
					catch (Exception) {
						// Torn down with the server at the end of the test.
					}
				}) { IsBackground = true }.Start();
			}

			WaitUntilBlocked(srv, LongPollers);

			// The real assertion: an unrelated request still completes promptly. When every request
			// shared the ThreadPool, this one waited on the pool's slow thread-injection heuristic.
			var sw = Stopwatch.StartNew();
			var res = Rpc.Post(srv.McpUrl, Rpc.Request(1234, "ping"));
			sw.Stop();

			Assert.Equal(HttpStatusCode.OK, res.Status);
			Assert.Equal(1234, (int?)res.Json["id"]);
			Assert.True(sw.ElapsedMilliseconds < 3000,
				$"a quick request took {sw.ElapsedMilliseconds} ms while {LongPollers} long-polls were blocked");
		}

		[Fact]
		public void Concurrent_ordinary_requests_all_get_their_own_answer() {
			using var srv = new McpTestServer();
			const int callers = 24;

			var results = new int?[callers];
			Parallel.For(0, callers, i => {
				var res = Rpc.Post(srv.McpUrl, Rpc.Request(1000 + i, "ping"));
				results[i] = (int?)res.Json["id"];
			});

			// Every caller must see its own id back — a shared-state bug would cross the wires.
			for (int i = 0; i < callers; i++)
				Assert.Equal(1000 + i, results[i]);
		}

		[Fact]
		public void The_server_still_serves_after_a_tool_throws() {
			using var srv = new McpTestServer();

			for (int i = 0; i < 5; i++) {
				var boom = Rpc.Post(srv.McpUrl, Rpc.CallTool(i, "stub_throw"));
				Assert.True(boom.ToolIsError);
			}

			var res = Rpc.Post(srv.McpUrl, Rpc.Request(50, "ping"));
			Assert.Equal(HttpStatusCode.OK, res.Status);
		}

		// Give the blocked requests time to actually reach their handlers before measuring; otherwise
		// the test could pass simply because the server had not picked them up yet.
		static void WaitUntilBlocked(McpTestServer srv, int expected) {
			var deadline = Environment.TickCount64 + 10000;
			while (Environment.TickCount64 < deadline) {
				if (srv.InFlightSlowCalls >= expected)
					return;
				Thread.Sleep(50);
			}
			throw new InvalidOperationException(
				$"only {srv.InFlightSlowCalls}/{expected} long-polls reached the server within 10s");
		}
	}
}
