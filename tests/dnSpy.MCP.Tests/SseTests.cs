using System.Net;
using Newtonsoft.Json.Linq;
using Xunit;

namespace dnSpy.MCP.Tests {
	/// <summary>
	/// The SSE stream is how an agent learns a process paused without polling. Writes happen on a
	/// dedicated sender thread precisely so a stalled reader cannot block the debug engine, so these
	/// tests care as much about the server staying healthy as about frames arriving.
	/// </summary>
	public class SseTests {
		[Fact]
		public void A_stream_request_is_accepted_and_stays_open() {
			using var srv = new McpTestServer();
			using var sse = new SseStream(srv.McpUrl);

			Assert.Equal(HttpStatusCode.OK, sse.Status);
		}

		[Fact]
		public void A_broadcast_reaches_a_connected_client() {
			using var srv = new McpTestServer();
			using var sse = new SseStream(srv.McpUrl);

			srv.Broadcast("notifications/paused", new JObject { ["pid"] = 4321, ["threadId"] = 99 });

			var frame = sse.WaitForFrame(f => f.Contains("notifications/paused"), 5000);
			Assert.NotNull(frame);

			var json = JObject.Parse(frame!.Substring(frame.IndexOf("data: ") + "data: ".Length));
			Assert.Equal("2.0", (string?)json["jsonrpc"]);
			Assert.Equal("notifications/paused", (string?)json["method"]);
			Assert.Equal(4321, (int?)json["params"]!["pid"]);
			Assert.Equal(99, (int?)json["params"]!["threadId"]);
		}

		[Fact]
		public void Every_connected_client_receives_a_broadcast() {
			using var srv = new McpTestServer();
			using var a = new SseStream(srv.McpUrl);
			using var b = new SseStream(srv.McpUrl);

			srv.Broadcast("notifications/paused", new JObject { ["pid"] = 7 });

			Assert.NotNull(a.WaitForFrame(f => f.Contains("\"pid\":7"), 5000));
			Assert.NotNull(b.WaitForFrame(f => f.Contains("\"pid\":7"), 5000));
		}

		// A dropped client must be pruned without taking the sender thread down with it, otherwise
		// every later notification is lost.
		[Fact]
		public void A_disconnected_client_does_not_break_later_broadcasts() {
			using var srv = new McpTestServer();
			var doomed = new SseStream(srv.McpUrl);
			using var survivor = new SseStream(srv.McpUrl);

			doomed.Dispose();
			// First broadcast discovers the dead socket and prunes it; the second proves the sender
			// thread is still running afterwards.
			srv.Broadcast("notifications/paused", new JObject { ["pid"] = 1 });
			srv.Broadcast("notifications/paused", new JObject { ["pid"] = 2 });

			Assert.NotNull(survivor.WaitForFrame(f => f.Contains("\"pid\":2"), 5000));

			var ping = Rpc.Post(srv.McpUrl, Rpc.Request(1, "ping"));
			Assert.Equal(HttpStatusCode.OK, ping.Status);
		}

		[Fact]
		public void Broadcasting_with_no_clients_connected_is_harmless() {
			using var srv = new McpTestServer();

			srv.Broadcast("notifications/paused", new JObject { ["pid"] = 1 });

			var ping = Rpc.Post(srv.McpUrl, Rpc.Request(1, "ping"));
			Assert.Equal(HttpStatusCode.OK, ping.Status);
		}

		// Idle proxies and NAT tables drop silent connections; the 15s comment heartbeat is what keeps
		// a long debugging session's stream alive.
		[Fact]
		public void An_idle_stream_receives_a_heartbeat() {
			using var srv = new McpTestServer();
			using var sse = new SseStream(srv.McpUrl);

			Assert.NotNull(sse.WaitForFrame(f => f.StartsWith(": ping"), 25000));
		}

		[Fact]
		public void A_stream_request_to_another_path_is_not_found() {
			using var srv = new McpTestServer();
			using var sse = new SseStream(srv.BaseUrl + "/events");

			Assert.Equal(HttpStatusCode.NotFound, sse.Status);
		}
	}
}
