using System.Net;
using System.Net.Http;
using Newtonsoft.Json.Linq;
using Xunit;

namespace dnSpy.MCP.Tests {
	/// <summary>
	/// The endpoint drives a debugger, so the guards in front of it are load-bearing: loopback-only
	/// Host, no browser Origin, optional bearer token, and a bounded request body.
	/// </summary>
	public class SecurityTests {
		const string Token = "correct-horse-battery-staple";

		// A malicious page cannot forge a loopback Host, but it can make the browser issue a request
		// with an Origin header. Rejecting Origin is what closes the DNS-rebinding pivot.
		[Fact]
		public void A_request_carrying_a_browser_Origin_is_rejected() {
			using var srv = new McpTestServer();
			var res = Rpc.Post(srv.McpUrl, Rpc.Request(1, "initialize"), origin: "http://evil.example");

			Assert.Equal(HttpStatusCode.Forbidden, res.Status);
		}

		[Fact]
		public void A_request_with_a_non_loopback_Host_is_rejected() {
			using var srv = new McpTestServer();
			var res = Rpc.Post(srv.McpUrl, Rpc.Request(1, "initialize"), host: "evil.example");

			Assert.Equal(HttpStatusCode.Forbidden, res.Status);
		}

		[Fact]
		public void The_localhost_spelling_of_the_loopback_host_is_accepted() {
			using var srv = new McpTestServer();
			var res = Rpc.Post(srv.McpUrl, Rpc.Request(1, "initialize"), host: $"localhost:{srv.Port}");

			Assert.Equal(HttpStatusCode.OK, res.Status);
		}

		[Fact]
		public void A_plain_GET_is_a_liveness_probe() {
			using var srv = new McpTestServer();
			var res = Rpc.Get(srv.BaseUrl + "/");

			Assert.Equal(HttpStatusCode.OK, res.Status);
			Assert.Contains("dnSpy", res.Body);
		}

		[Fact]
		public void A_POST_to_another_path_is_not_found() {
			using var srv = new McpTestServer();
			var res = Rpc.Post(srv.BaseUrl + "/admin", Rpc.Request(1, "initialize"));

			Assert.Equal(HttpStatusCode.NotFound, res.Status);
		}

		[Fact]
		public void An_unsupported_verb_is_rejected() {
			using var srv = new McpTestServer();
			var res = Rpc.Send(srv.McpUrl, HttpMethod.Put, "{}");

			Assert.Equal(HttpStatusCode.MethodNotAllowed, res.Status);
		}

		// ---- bearer token ----

		[Fact]
		public void Without_a_configured_token_requests_are_unauthenticated() {
			using var srv = new McpTestServer();
			var res = Rpc.Post(srv.McpUrl, Rpc.Request(1, "initialize"));

			Assert.Equal(HttpStatusCode.OK, res.Status);
		}

		// Authentication is on by default now, so a server with no token means somebody opted out —
		// which must never happen quietly.
		[Fact]
		public void Starting_without_a_token_is_not_silent() {
			using var srv = new McpTestServer();

			Assert.True(srv.LoggedContaining("WITHOUT authentication"),
				"expected the server to warn that the endpoint is unauthenticated");
		}

		// A 401 that only says "no" leaves the caller stuck; the token is on their own disk.
		[Fact]
		public void An_unauthorized_response_says_where_to_find_the_token() {
			var tokenFile = @"C:\somewhere\mcp-token.txt";
			using var srv = new McpTestServer(Token, tokenFilePath: tokenFile);

			var res = Rpc.Post(srv.McpUrl, Rpc.Request(1, "initialize"));

			Assert.Equal(HttpStatusCode.Unauthorized, res.Status);
			Assert.Contains(tokenFile, res.Body);
			Assert.Contains("Bearer", res.Body);
		}

		[Fact]
		public void An_unauthorized_response_points_at_the_log_when_the_token_never_reached_disk() {
			using var srv = new McpTestServer(Token);

			var res = Rpc.Post(srv.McpUrl, Rpc.Request(1, "initialize"));

			Assert.Equal(HttpStatusCode.Unauthorized, res.Status);
			Assert.Contains("dnSpy.MCP.log", res.Body);
		}

		[Fact]
		public void A_configured_token_rejects_a_request_that_omits_it() {
			using var srv = new McpTestServer(Token);
			var res = Rpc.Post(srv.McpUrl, Rpc.Request(1, "initialize"));

			Assert.Equal(HttpStatusCode.Unauthorized, res.Status);
		}

		[Theory]
		[InlineData("wrong-token-same-length-xxxxx")] // same length, different content
		[InlineData("short")]                          // shorter than the real token
		[InlineData("correct-horse-battery-staple-and-then-some")] // longer, correct prefix
		[InlineData("")]                               // empty
		public void A_configured_token_rejects_a_wrong_token(string wrong) {
			using var srv = new McpTestServer(Token);
			var res = Rpc.Post(srv.McpUrl, Rpc.Request(1, "initialize"), token: wrong);

			Assert.Equal(HttpStatusCode.Unauthorized, res.Status);
		}

		[Fact]
		public void A_configured_token_accepts_the_right_token() {
			using var srv = new McpTestServer(Token);
			var res = Rpc.Post(srv.McpUrl, Rpc.Request(1, "initialize"), token: Token);

			Assert.Equal(HttpStatusCode.OK, res.Status);
			Assert.Equal("dnSpy", (string?)res.Result["serverInfo"]!["name"]);
		}

		// The right secret under the wrong scheme must not authenticate — otherwise a client that
		// merely looks plausible gets in.
		[Theory]
		[InlineData("Basic " + Token)]
		[InlineData(Token)]
		[InlineData("bearer " + Token)] // scheme match is case-sensitive by design
		public void A_non_bearer_authorization_header_is_rejected(string header) {
			using var srv = new McpTestServer(Token);
			var res = Rpc.PostWithRawAuth(srv.McpUrl, Rpc.Request(1, "initialize"), header);

			Assert.Equal(HttpStatusCode.Unauthorized, res.Status);
		}

		// ---- request body cap ----

		// The cap must hold whether or not the client declares a length. A chunked upload reports
		// ContentLength64 == -1, which used to skip the check entirely and stream without bound.
		[Fact]
		public void An_oversized_chunked_body_is_refused_and_the_server_survives() {
			using var srv = new McpTestServer();

			var res = Rpc.PostChunked(srv.McpUrl, 6 * 1024 * 1024);
			Assert.Equal(HttpStatusCode.RequestEntityTooLarge, res.Status);

			// The point of the cap is that the server stays healthy, so prove it still answers.
			var after = Rpc.Post(srv.McpUrl, Rpc.Request(99, "ping"));
			Assert.Equal(HttpStatusCode.OK, after.Status);
			Assert.Equal(99, (int?)after.Json["id"]);
		}

		[Fact]
		public void An_oversized_declared_body_is_refused_and_the_server_survives() {
			using var srv = new McpTestServer();

			var big = new string('a', 6 * 1024 * 1024);
			var res = Rpc.PostRawTolerant(srv.McpUrl, "{\"padding\":\"" + big + "\"}");
			Assert.Equal(HttpStatusCode.RequestEntityTooLarge, res.Status);

			var after = Rpc.Post(srv.McpUrl, Rpc.Request(99, "ping"));
			Assert.Equal(HttpStatusCode.OK, after.Status);
		}

		[Fact]
		public void A_body_just_under_the_cap_is_still_processed() {
			using var srv = new McpTestServer();

			// Valid JSON-RPC with a large but legal argument: proves the cap rejects on size, not on
			// "anything big", and that the streaming read reassembles a multi-buffer body correctly.
			var payload = Rpc.CallTool(1, "stub_read", new JObject { ["text"] = new string('x', 1024 * 1024) });
			var res = Rpc.Post(srv.McpUrl, payload);

			Assert.Equal(HttpStatusCode.OK, res.Status);
			Assert.False(res.ToolIsError);
			Assert.Contains(new string('x', 4096), res.ToolText);
		}
	}
}
