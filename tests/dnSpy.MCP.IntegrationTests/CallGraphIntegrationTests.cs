using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace dnSpy.MCP.IntegrationTests {
	/// <summary>
	/// The call_graph static tool: BFS of callers/callees around a root method, no debug session needed.
	/// Every assertion here is anchored to the fixture's deterministic Level1 -> Level2 -> Level3 chain
	/// (Program.cs) so the shape of the graph is a constant you can verify by hand.
	///
	/// Each test would fail before call_graph is registered in StaticTools.Create(): the CallJson tests
	/// error out because the server reports an unknown tool (DbgToolException), and the unknown-method
	/// test's error text would be "unknown tool" rather than the method name it asserts on.
	/// </summary>
	[Collection("dnSpy")]
	public class CallGraphIntegrationTests : IDisposable {
		public CallGraphIntegrationTests() => Dbg.Reset();
		public void Dispose() => Dbg.Reset();

		// Fails before the tool exists: call_graph is unregistered, so CallJson throws on the tool error.
		[DbgFact]
		public void Callees_of_a_method_include_a_direct_callee() {
			var res = Dbg.CallJson("call_graph", new JObject {
				["module"] = Dbg.FixtureDll(),
				["method"] = "DbgTest.Program.Level1",
				["direction"] = "callees",
				["depth"] = 1,
			});

			// The root is echoed with a method token (0x06...), and the reported direction round-trips.
			Assert.Contains("Level1", (string?)res["root"]);
			Assert.StartsWith("0x06", (string?)res["rootToken"]);
			Assert.Equal("callees", (string?)res["direction"]);

			// Level1 calls Level2 directly (Program.cs: `return Level2(one);`), so Level2 is a node and
			// there is a Level1 -> Level2 edge.
			var nodes = (JArray)res["nodes"]!;
			var edges = (JArray)res["edges"]!;
			Assert.Contains(nodes, n => ((string?)n["name"])?.Contains("Level2") == true);
			Assert.Contains(edges, e =>
				((string?)e["from"])?.Contains("Level1") == true &&
				((string?)e["to"])?.Contains("Level2") == true);
			Assert.All(edges, e => Assert.StartsWith("0x06", (string?)e["toToken"]));
			Assert.False((bool)res["truncated"]!);
		}

		// Fails before the tool exists: unregistered tool -> DbgToolException from CallJson.
		[DbgFact]
		public void Callers_of_a_leaf_include_its_caller() {
			var res = Dbg.CallJson("call_graph", new JObject {
				["module"] = Dbg.FixtureDll(),
				["method"] = "DbgTest.Program.Level3",
				["direction"] = "callers",
				["depth"] = 1,
			});
			Assert.Equal("callers", (string?)res["direction"]);

			// Only Level2 calls Level3 in the fixture, so Level2 is a node and the edge points Level2 -> Level3
			// (edges are always oriented caller -> callee, whichever direction was requested).
			var nodes = (JArray)res["nodes"]!;
			var edges = (JArray)res["edges"]!;
			Assert.Contains(nodes, n => ((string?)n["name"])?.Contains("Level2") == true);
			Assert.Contains(edges, e =>
				((string?)e["from"])?.Contains("Level2") == true &&
				((string?)e["to"])?.Contains("Level3") == true);
		}

		// Fails before the tool exists: unregistered tool -> DbgToolException from CallJson.
		[DbgFact]
		public void Depth_greater_than_one_reaches_the_second_hop() {
			JArray NodesAtDepth(int depth) => (JArray)Dbg.CallJson("call_graph", new JObject {
				["module"] = Dbg.FixtureDll(),
				["method"] = "DbgTest.Program.Level1",
				["direction"] = "callees",
				["depth"] = depth,
			})["nodes"]!;

			// Level1 -> Level2 (hop 1) -> Level3 (hop 2). Level3 is only reachable once depth allows two hops.
			var shallow = NodesAtDepth(1);
			var deep = NodesAtDepth(2);
			Assert.DoesNotContain(shallow, n => ((string?)n["name"])?.Contains("Level3") == true);
			Assert.Contains(deep, n => ((string?)n["name"])?.Contains("Level3") == true);
		}

		// Fails before the tool exists: the error would be an "unknown tool" message, not one naming the
		// missing method, so the Contains assertion could not pass.
		[DbgFact]
		public void An_unknown_root_method_is_reported() {
			var error = Dbg.CallExpectingError("call_graph", new JObject {
				["module"] = Dbg.FixtureDll(),
				["method"] = "DbgTest.Program.NoSuchMethod",
			});
			Assert.Contains("NoSuchMethod", error);
		}

		// Fails before the tool exists: unregistered tool -> DbgToolException, so `truncated` is never read.
		[DbgFact]
		public void Max_nodes_caps_the_graph_and_flags_truncation() {
			// Main has several in-module callees, but a cap of one leaves room for the root alone, so the
			// first neighbour trips the cap and truncated is set.
			var res = Dbg.CallJson("call_graph", new JObject {
				["module"] = Dbg.FixtureDll(),
				["method"] = "DbgTest.Program.Main",
				["direction"] = "callees",
				["depth"] = 2,
				["max_nodes"] = 1,
			});

			Assert.True((bool)res["truncated"]!);
			Assert.Single((JArray)res["nodes"]!); // only the root fit under the cap
		}
	}
}
