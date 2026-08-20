using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace dnSpy.MCP.IntegrationTests {
	/// <summary>
	/// Static analysis tools: decompile, list_types, list_methods. These need the decompiler and the
	/// document service but NOT a debug session — an agent can read and explore an assembly on disk
	/// without ever launching it. Every test here runs against the fixture with nothing debugging,
	/// which is also the property that makes them safe and fast.
	/// </summary>
	[Collection("dnSpy")]
	public class StaticIntegrationTests : IDisposable {
		public StaticIntegrationTests() => Dbg.Reset();
		public void Dispose() => Dbg.Reset();

		[DbgFact]
		public void Types_are_listed_with_tokens_and_kinds() {
			var res = Dbg.CallJson("list_types", new JObject {
				["module"] = Dbg.FixtureDll(),
				["filter"] = "DbgTest.*",
			});
			var types = (JArray)res["types"]!;

			var program = types.FirstOrDefault(t => (string?)t["name"] == "DbgTest.Program");
			Assert.NotNull(program);
			Assert.StartsWith("0x02", (string?)program!["token"]); // TypeDef tokens start 0x02
			Assert.Equal("class", (string?)program["kind"]);
			Assert.Contains(types, t => (string?)t["name"] == "DbgTest.Node");
		}

		[DbgFact]
		public void The_type_filter_is_a_wildcard() {
			var all = (long)Dbg.CallJson("list_types", new JObject { ["module"] = Dbg.FixtureDll() })["matched"]!;
			var filtered = (long)Dbg.CallJson("list_types", new JObject {
				["module"] = Dbg.FixtureDll(),
				["filter"] = "DbgTest.Outer*",
			})["matched"]!;

			Assert.True(filtered >= 2, "expected Outer and Outer.Inner"); // Outer, Outer+Inner
			Assert.True(filtered < all, "the filter should not match every type in the module");
		}

		[DbgFact]
		public void Methods_are_listed_with_the_same_tokens_the_debugger_uses() {
			var res = Dbg.CallJson("list_methods", new JObject {
				["module"] = Dbg.FixtureDll(),
				["type"] = "DbgTest.Program",
			});
			var methods = (JArray)res["methods"]!;

			// Program.Add has two overloads; both must appear with method tokens (0x06...).
			var adds = methods.Where(m => (string?)m["name"] == "Add").ToArray();
			Assert.Equal(2, adds.Length);
			Assert.All(adds, m => Assert.StartsWith("0x06", (string?)m["token"]));
			Assert.All(adds, m => Assert.True((bool?)m["static"]));

			// The token list_methods reports must be the one bp_add_method resolves the name to.
			var byName = Dbg.TokenOf("DbgTest.Program.Add"); // first overload
			Assert.Contains(adds, m => (string?)m["token"] == byName);
		}

		[DbgFact]
		public void A_method_is_decompiled_to_csharp_by_name() {
			var src = Dbg.Call("decompile", new JObject {
				["module"] = Dbg.FixtureDll(),
				["method"] = "DbgTest.Program.Add",
			});

			// Both overloads, decompiled to real C#.
			Assert.Contains("int Add(int a, int b)", src);
			Assert.Contains("return a + b;", src);
			Assert.Contains("int Add(int a, int b, int c)", src);
			Assert.Contains("return a + b + c;", src);
		}

		[DbgFact]
		public void A_member_is_decompiled_by_metadata_token() {
			// Discover the token the way an agent would, then decompile it — the two tools compose.
			var methods = (JArray)Dbg.CallJson("list_methods", new JObject {
				["module"] = Dbg.FixtureDll(),
				["type"] = "DbgTest.Program",
			})["methods"]!;
			var level3 = methods.First(m => (string?)m["name"] == "Level3");

			var src = Dbg.Call("decompile", new JObject {
				["module"] = Dbg.FixtureDll(),
				["token"] = (string?)level3["token"],
			});

			Assert.Contains("Level3", src);
			Assert.Contains("three + 100", src);
		}

		[DbgFact]
		public void A_whole_type_can_be_decompiled() {
			var src = Dbg.Call("decompile", new JObject {
				["module"] = Dbg.FixtureDll(),
				["type"] = "DbgTest.Outer",
			});

			// The nested Inner.Ping is part of the type.
			Assert.Contains("Inner", src);
			Assert.Contains("pong", src);
		}

		// The headline property: none of this needs a running process.
		[DbgFact]
		public void Decompiling_needs_no_debug_session() {
			Assert.False((bool)Dbg.Status()["isDebugging"]!, "precondition: nothing is debugging");

			var src = Dbg.Call("decompile", new JObject {
				["module"] = Dbg.FixtureDll(),
				["method"] = "DbgTest.Program.Level1",
			});

			Assert.Contains("Level2", src); // Level1 calls Level2
			Assert.False((bool)Dbg.Status()["isDebugging"]!, "decompile must not have started a session");
		}

		[DbgFact]
		public void An_unknown_module_is_reported() {
			var error = Dbg.CallExpectingError("list_types", new JObject { ["module"] = "no-such.dll" });
			Assert.Contains("no-such.dll", error);
		}

		[DbgFact]
		public void An_unknown_type_is_reported() {
			var error = Dbg.CallExpectingError("list_methods", new JObject {
				["module"] = Dbg.FixtureDll(),
				["type"] = "DbgTest.NoSuchType",
			});
			Assert.Contains("NoSuchType", error);
		}

		[DbgFact]
		public void Decompile_requires_a_selector() {
			var error = Dbg.CallExpectingError("decompile", new JObject { ["module"] = Dbg.FixtureDll() });
			Assert.Contains("method", error, StringComparison.OrdinalIgnoreCase);
		}

		// ---- format=il ----

		[DbgFact]
		public void A_method_can_be_disassembled_to_il() {
			var il = Dbg.Call("decompile", new JObject {
				["module"] = Dbg.FixtureDll(),
				["method"] = "DbgTest.Program.Add",
				["format"] = "il",
			});

			// IL, not C#: opcodes and a stack directive, and no C# 'return a + b;'.
			Assert.Contains(".maxstack", il);
			Assert.Contains("ldarg", il);
			Assert.Contains("ret", il);
			Assert.DoesNotContain("return a + b;", il);
		}

		[DbgFact]
		public void An_unknown_format_is_rejected() {
			var error = Dbg.CallExpectingError("decompile", new JObject {
				["module"] = Dbg.FixtureDll(),
				["method"] = "DbgTest.Program.Add",
				["format"] = "python",
			});
			Assert.Contains("format", error, StringComparison.OrdinalIgnoreCase);
		}

		// ---- search ----

		[DbgFact]
		public void A_string_literal_is_found_with_the_method_that_uses_it() {
			var res = Dbg.CallJson("search", new JObject {
				["module"] = Dbg.FixtureDll(),
				["query"] = "hello",
				["kind"] = "strings",
			});
			var hits = (JArray)res["hits"]!;

			// The fixture loads "hello" in Inspect; the hit carries the method and its token.
			var hit = hits.FirstOrDefault(h => (string?)h["kind"] == "string");
			Assert.NotNull(hit);
			Assert.Equal("hello", (string?)hit!["value"]);
			Assert.Contains("Inspect", (string?)hit["name"]);
			Assert.StartsWith("0x06", (string?)hit["token"]);
		}

		[DbgFact]
		public void Member_names_are_found_by_dotted_pattern_and_by_simple_name() {
			var byDotted = (JArray)Dbg.CallJson("search", new JObject {
				["module"] = Dbg.FixtureDll(),
				["query"] = "*.Level*",
				["kind"] = "names",
			})["hits"]!;
			// The signature form uses '::', which a dotted query would miss — this proves the friendly
			// match. Level1/Level2/Level3 are all methods.
			Assert.Equal(3, byDotted.Count(h => (string?)h["kind"] == "method"));
			Assert.All(byDotted.Where(h => (string?)h["kind"] == "method"),
				h => Assert.Contains("DbgTest.Program.Level", (string?)h["name"]));

			var bySimple = (JArray)Dbg.CallJson("search", new JObject {
				["module"] = Dbg.FixtureDll(),
				["query"] = "Level?",
				["kind"] = "names",
			})["hits"]!;
			Assert.Equal(3, bySimple.Count(h => (string?)h["kind"] == "method"));
		}

		// The token a name search reports drives decompile — the tools compose.
		[DbgFact]
		public void A_search_hit_token_decompiles() {
			var hit = ((JArray)Dbg.CallJson("search", new JObject {
				["module"] = Dbg.FixtureDll(),
				["query"] = "*.Level3",
				["kind"] = "names",
			})["hits"]!).First(h => (string?)h["kind"] == "method");

			var src = Dbg.Call("decompile", new JObject {
				["module"] = Dbg.FixtureDll(),
				["token"] = (string?)hit["token"],
			});
			Assert.Contains("three + 100", src);
		}

		// ---- find_references ----

		[DbgFact]
		public void The_callers_of_a_method_are_found() {
			var res = Dbg.CallJson("find_references", new JObject {
				["module"] = Dbg.FixtureDll(),
				["method"] = "DbgTest.Program.Level2",
			});
			var callers = (JArray)res["callers"]!;

			// Only Level1 calls Level2 in the fixture.
			Assert.Contains(callers, c => ((string?)c["caller"])?.Contains("Level1") == true);
			Assert.All(callers, c => Assert.StartsWith("0x", (string?)c["ilOffset"]));
			Assert.True((long)res["scannedMethods"]! > 0);
		}

		[DbgFact]
		public void Callers_can_be_found_by_target_token() {
			var addToken = Dbg.TokenOf("DbgTest.Program.Add");
			var callers = (JArray)Dbg.CallJson("find_references", new JObject {
				["module"] = Dbg.FixtureDll(),
				["token"] = addToken,
			})["callers"]!;

			// Main calls Add in the loop.
			Assert.Contains(callers, c => ((string?)c["caller"])?.Contains("Main") == true);
		}

		[DbgFact]
		public void Find_references_requires_a_target() {
			var error = Dbg.CallExpectingError("find_references", new JObject { ["module"] = Dbg.FixtureDll() });
			Assert.Contains("method", error, StringComparison.OrdinalIgnoreCase);
		}
	}
}
