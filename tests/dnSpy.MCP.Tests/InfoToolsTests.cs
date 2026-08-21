using System;
using System.Collections.Generic;
using System.Linq;
using dnSpy.MCP.Tools;
using Newtonsoft.Json.Linq;
using Xunit;

namespace dnSpy.MCP.Tests {
	/// <summary>
	/// The dnspy_info contract. Clients use it to tell one dnSpy instance from another before they
	/// drive a debugger, so every field has to be present, correctly typed, and true at call time
	/// rather than at construction time.
	/// </summary>
	public class InfoToolsTests {
		static ToolDef Tool(int port = 27115, bool authRequired = false,
			string authSource = InfoTools.AuthSourceNone, Func<int>? toolCount = null) =>
			new InfoTools(port, () => authRequired, () => authSource, toolCount ?? (() => 33)).Create().Single();

		static JObject Info(int port = 27115, bool authRequired = false,
			string authSource = InfoTools.AuthSourceNone, Func<int>? toolCount = null) =>
			JObject.Parse(Tool(port, authRequired, authSource, toolCount).Handler(new JObject()));

		[Fact]
		public void Info_reports_every_contract_field_with_the_right_type() {
			var info = Info(port: 12345, authRequired: true, authSource: InfoTools.AuthSourceFile,
				toolCount: () => 33);

			Assert.Equal("dnSpy", (string?)info["server"]!["name"]);
			Assert.Equal("1.0.0", (string?)info["server"]!["version"]);
			Assert.Equal(JTokenType.Integer, info["server"]!["toolCount"]!.Type);
			Assert.Equal(33, (int?)info["server"]!["toolCount"]);
			Assert.Equal(JTokenType.String, info["dnSpyVersion"]!.Type);
			Assert.Equal(JTokenType.Integer, info["port"]!.Type);
			Assert.Equal(12345, (int?)info["port"]);
			Assert.Equal(JTokenType.Boolean, info["auth"]!["required"]!.Type);
			Assert.True((bool?)info["auth"]!["required"]);
			Assert.Equal(JTokenType.String, info["settingsFile"]!.Type);
		}

		// The whole point of the tool is answering "which build am I talking to", so an empty or
		// placeholder version would make it useless.
		[Fact]
		public void Info_reports_a_real_dnSpy_assembly_version() {
			var version = (string?)Info()["dnSpyVersion"];

			Assert.False(string.IsNullOrWhiteSpace(version));
			Assert.NotEqual("unknown", version);
			Assert.Equal(typeof(ToolDef).Assembly.GetName().Version!.ToString(), version);
		}

		[Fact]
		public void Info_reports_the_settings_file_dnSpy_actually_uses() {
			var path = (string?)Info()["settingsFile"];

			Assert.Equal(dnSpy.Contracts.App.AppDirectories.SettingsFilename, path);
			Assert.EndsWith("dnSpy.xml", path!);
		}

		[Theory]
		[InlineData(InfoTools.AuthSourceEnv, true)]
		[InlineData(InfoTools.AuthSourceFile, true)]
		[InlineData(InfoTools.AuthSourceNone, false)]
		public void Auth_reports_where_the_token_came_from(string source, bool required) {
			var auth = (JObject)Info(authRequired: required, authSource: source)["auth"]!;

			Assert.Equal(source, (string?)auth["source"]);
			Assert.Equal(required, (bool?)auth["required"]);
		}

		// The host builds every tool before it can know the total, so the count must be read when the
		// tool is called. Capturing it at construction would report a stale, too-small number.
		[Fact]
		public void ToolCount_is_evaluated_at_call_time_not_at_construction() {
			var tools = new List<ToolDef>();
			var tool = new InfoTools(1, () => false, () => InfoTools.AuthSourceNone, () => tools.Count)
				.Create().Single();
			tools.Add(tool);

			Assert.Equal(1, (int?)JObject.Parse(tool.Handler(new JObject()))["server"]!["toolCount"]);

			tools.AddRange(McpTestServer.StubTools());

			// 1 info tool + StubTools() (which now includes stub_structured).
			Assert.Equal(1 + McpTestServer.StubTools().Count, (int?)JObject.Parse(tool.Handler(new JObject()))["server"]!["toolCount"]);
		}

		[Fact]
		public void The_tool_takes_no_arguments() {
			var tool = Tool();

			Assert.Equal("dnspy_info", tool.Name);
			Assert.Equal("object", (string?)tool.InputSchema["type"]);
			Assert.Empty((JObject)tool.InputSchema["properties"]!);
			Assert.Null(tool.InputSchema["required"]);
		}

		[Fact]
		public void ToolsList_annotates_dnspy_info_as_read_only() {
			var tools = McpTestServer.StubTools().Concat(new[] { Tool() }).ToList();
			using var srv = new McpTestServer(tools: tools);
			var res = Rpc.Post(srv.McpUrl, Rpc.Request(1, "tools/list"));

			var listed = ((JArray)res.Result["tools"]!).Single(t => (string?)t["name"] == "dnspy_info");
			Assert.True((bool?)listed["annotations"]!["readOnlyHint"]);
			Assert.Null(listed["annotations"]!["destructiveHint"]);
		}

		[Fact]
		public void CallTool_returns_indented_json_over_the_wire() {
			var tools = new List<ToolDef> { Tool(port: 4242) };
			using var srv = new McpTestServer(tools: tools);
			var res = Rpc.Post(srv.McpUrl, Rpc.CallTool(2, "dnspy_info"));

			Assert.False(res.ToolIsError);
			Assert.Contains("\n", res.ToolText);
			Assert.Equal(4242, (int?)JObject.Parse(res.ToolText)["port"]);
		}
	}
}
