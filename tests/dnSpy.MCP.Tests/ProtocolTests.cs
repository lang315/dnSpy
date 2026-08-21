using System.Linq;
using System.Net;
using Newtonsoft.Json.Linq;
using Xunit;

namespace dnSpy.MCP.Tests {
	/// <summary>JSON-RPC / MCP protocol surface: initialize, tools/list, tools/call, error shapes.</summary>
	public class ProtocolTests {
		[Fact]
		public void Initialize_reports_server_identity_and_tool_capability() {
			using var srv = new McpTestServer();
			var res = Rpc.Post(srv.McpUrl, Rpc.Request(1, "initialize", new JObject()));

			Assert.Equal(HttpStatusCode.OK, res.Status);
			Assert.Equal("dnSpy", (string?)res.Result["serverInfo"]!["name"]);
			Assert.NotNull(res.Result["capabilities"]!["tools"]);
			Assert.Equal("2.0", (string?)res.Json["jsonrpc"]);
			Assert.Equal(1, (int?)res.Json["id"]);
		}

		[Theory]
		[InlineData("2025-06-18")]
		[InlineData("2025-03-26")]
		[InlineData("2024-11-05")]
		public void Initialize_echoes_a_supported_protocol_version(string version) {
			using var srv = new McpTestServer();
			var res = Rpc.Post(srv.McpUrl, Rpc.Request(1, "initialize",
				new JObject { ["protocolVersion"] = version }));

			Assert.Equal(version, (string?)res.Result["protocolVersion"]);
		}

		[Fact]
		public void Initialize_falls_back_to_the_default_for_an_unknown_version() {
			using var srv = new McpTestServer();
			var res = Rpc.Post(srv.McpUrl, Rpc.Request(1, "initialize",
				new JObject { ["protocolVersion"] = "1999-01-01" }));

			Assert.Equal("2024-11-05", (string?)res.Result["protocolVersion"]);
		}

		[Fact]
		public void Ping_returns_an_empty_result() {
			using var srv = new McpTestServer();
			var res = Rpc.Post(srv.McpUrl, Rpc.Request(7, "ping"));

			Assert.Equal(HttpStatusCode.OK, res.Status);
			Assert.Empty(res.Result.Properties());
		}

		[Fact]
		public void ToolsList_describes_every_tool_with_a_schema() {
			using var srv = new McpTestServer();
			var res = Rpc.Post(srv.McpUrl, Rpc.Request(2, "tools/list"));
			var tools = (JArray)res.Result["tools"]!;

			Assert.Equal(McpTestServer.StubTools().Count, tools.Count);
			foreach (var tool in tools) {
				Assert.False(string.IsNullOrWhiteSpace((string?)tool["name"]));
				Assert.False(string.IsNullOrWhiteSpace((string?)tool["description"]));
				Assert.Equal("object", (string?)tool["inputSchema"]!["type"]);
			}
		}

		[Fact]
		public void ToolsList_annotates_read_only_and_destructive_tools() {
			using var srv = new McpTestServer();
			var res = Rpc.Post(srv.McpUrl, Rpc.Request(2, "tools/list"));
			var tools = (JArray)res.Result["tools"]!;

			JToken Tool(string name) => tools.Single(t => (string?)t["name"] == name);

			Assert.True((bool?)Tool("stub_read")["annotations"]!["readOnlyHint"]);
			Assert.True((bool?)Tool("stub_destroy")["annotations"]!["destructiveHint"]);
			Assert.Null(Tool("stub_plain")["annotations"]);
		}

		[Fact]
		public void ToolsList_marks_required_arguments_in_the_schema() {
			using var srv = new McpTestServer();
			var res = Rpc.Post(srv.McpUrl, Rpc.Request(2, "tools/list"));
			var destroy = ((JArray)res.Result["tools"]!).Single(t => (string?)t["name"] == "stub_destroy");

			var required = (JArray)destroy["inputSchema"]!["required"]!;
			Assert.Equal(new[] { "target" }, required.Select(t => (string?)t));
		}

		[Fact]
		public void CallTool_passes_arguments_through_to_the_handler() {
			using var srv = new McpTestServer();
			var res = Rpc.Post(srv.McpUrl, Rpc.CallTool(3, "stub_read", new JObject { ["text"] = "hello" }));

			Assert.False(res.ToolIsError);
			Assert.Contains("hello", res.ToolText);
		}

		// MCP models a tool that fails as a successful call carrying isError, not as a JSON-RPC error.
		// Getting this wrong makes clients treat a failed debugger action as a broken server.
		[Fact]
		public void A_throwing_tool_is_reported_as_an_isError_result_not_a_protocol_error() {
			using var srv = new McpTestServer();
			var res = Rpc.Post(srv.McpUrl, Rpc.CallTool(4, "stub_throw"));

			Assert.Equal(HttpStatusCode.OK, res.Status);
			Assert.Null(res.Json["error"]);
			Assert.True(res.ToolIsError);
			Assert.Equal("stub failure message", res.ToolText);
		}

		[Fact]
		public void An_unknown_tool_is_an_invalid_params_error() {
			using var srv = new McpTestServer();
			var res = Rpc.Post(srv.McpUrl, Rpc.CallTool(5, "does_not_exist"));

			Assert.Equal(-32602, res.ErrorCode);
		}

		[Fact]
		public void An_unknown_method_is_a_method_not_found_error() {
			using var srv = new McpTestServer();
			var res = Rpc.Post(srv.McpUrl, Rpc.Request(6, "no/such/method"));

			Assert.Equal(-32601, res.ErrorCode);
		}

		[Fact]
		public void Malformed_json_is_a_parse_error() {
			using var srv = new McpTestServer();
			var res = Rpc.PostRaw(srv.McpUrl, "{ not json");

			Assert.Equal(HttpStatusCode.BadRequest, res.Status);
			Assert.Equal(-32700, res.ErrorCode);
			Assert.Equal(JTokenType.Null, res.Json["id"]!.Type);
		}

		// A notification carries no id and must not get a response body — real clients send
		// notifications/initialized right after initialize.
		[Fact]
		public void A_notification_is_accepted_with_no_response_body() {
			using var srv = new McpTestServer();
			var res = Rpc.Post(srv.McpUrl, Rpc.Notification("notifications/initialized"));

			Assert.Equal(HttpStatusCode.Accepted, res.Status);
			Assert.Equal("", res.Body);
		}

		// ---- Structured content (MCP spec 2025-06-18) ----
		// stub_structured (in StubTools) is the only tool that declares an outputSchema and returns a
		// JSON object. All four below are red before the change: ToolDef had no OutputSchema, ListTools
		// never emitted `outputSchema`, and CallTool never emitted `structuredContent`.

		// A tool that opts in advertises its outputSchema (of type "object") in tools/list.
		[Fact]
		public void ToolsList_advertises_outputSchema_for_a_structured_tool() {
			using var srv = new McpTestServer();
			var res = Rpc.Post(srv.McpUrl, Rpc.Request(2, "tools/list"));
			var structured = ((JArray)res.Result["tools"]!).Single(t => (string?)t["name"] == "stub_structured");

			Assert.Equal("object", (string?)structured["outputSchema"]!["type"]);
		}

		// Back-compat: a tool that did not opt in advertises no outputSchema at all.
		[Fact]
		public void ToolsList_omits_outputSchema_for_a_tool_that_did_not_opt_in() {
			using var srv = new McpTestServer();
			var res = Rpc.Post(srv.McpUrl, Rpc.Request(2, "tools/list"));
			var plain = ((JArray)res.Result["tools"]!).Single(t => (string?)t["name"] == "stub_read");

			Assert.Null(plain["outputSchema"]);
		}

		// A structured tool returns BOTH the text content array (unchanged) AND a structuredContent
		// object that round-trips that same text.
		[Fact]
		public void CallTool_on_a_structured_tool_returns_text_and_matching_structuredContent() {
			using var srv = new McpTestServer();
			var res = Rpc.Post(srv.McpUrl, Rpc.CallTool(3, "stub_structured", new JObject { ["text"] = "hi" }));

			Assert.False(res.ToolIsError);
			var fromText = JObject.Parse(res.ToolText); // the legacy text contract still holds
			Assert.Equal("hi", (string?)fromText["echo"]);
			var structured = (JObject)res.Result["structuredContent"]!;
			Assert.True(JToken.DeepEquals(fromText, structured)); // structuredContent == parsed text
		}

		// Back-compat: a tool without an outputSchema returns text content only, no structuredContent.
		[Fact]
		public void CallTool_on_a_plain_tool_returns_no_structuredContent() {
			using var srv = new McpTestServer();
			var res = Rpc.Post(srv.McpUrl, Rpc.CallTool(4, "stub_read", new JObject { ["text"] = "hi" }));

			Assert.False(res.ToolIsError);
			Assert.Null(res.Result["structuredContent"]);
		}
	}
}
