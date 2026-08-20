using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace dnSpy.MCP.IntegrationTests {
	/// <summary>
	/// dump_module / mem_load: analyse a module loaded in the live debuggee. Tier-2 debug tests — they
	/// start the fixture and break at entry, then dump/load its own module.
	/// NOTE: the fixture's in-memory image equals its on-disk image, so these prove the tools *work* (produce
	/// a valid PE / a decompilable module), not that they read from memory vs disk — a self-modifying fixture
	/// is out of scope. The real memory-vs-disk payoff is exercised by hand against packed targets.
	/// </summary>
	[Collection("dnSpy")]
	public class LiveModuleIntegrationTests : IDisposable {
		public LiveModuleIntegrationTests() { Dbg.Reset(); PauseAtEntry(); }
		public void Dispose() => Dbg.Reset();

		static void PauseAtEntry() {
			Dbg.Call("dbg_start", new JObject { ["path"] = Dbg.FixtureDll(), ["break_at_entry"] = true });
			Dbg.WaitForBreak();
		}

		[DbgFact]
		public void A_loaded_module_is_dumped_and_reanalysable() {
			var res = Dbg.CallJson("dump_module", new JObject { ["module"] = "dbgtest.dll" });
			var path = (string?)res["savedTo"]!;
			try {
				Assert.True((long)res["bytes"]! > 0);
				Assert.True(System.IO.File.Exists(path));
				// A valid PE image starts with the MZ header.
				var head = System.IO.File.ReadAllBytes(path);
				Assert.Equal(0x4D, head[0]);
				Assert.Equal(0x5A, head[1]);
				// The dump is a real assembly the static tools can read.
				var types = (JArray)Dbg.CallJson("list_types", new JObject {
					["module"] = path,
					["filter"] = "DbgTest.*",
				})["types"]!;
				Assert.Contains(types, t => (string?)t["name"] == "DbgTest.Program");
				var src = Dbg.Call("decompile", new JObject { ["module"] = path, ["method"] = "DbgTest.Program.Add" });
				Assert.Contains("return a + b;", src);
			}
			finally {
				if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
			}
		}

		[DbgFact]
		public void A_module_loaded_from_memory_is_analysable_by_name() {
			var loaded = (string?)Dbg.CallJson("mem_load", new JObject { ["module"] = "dbgtest.dll" })["loaded"]!;
			Assert.False(string.IsNullOrEmpty(loaded));

			// The in-memory module is now reachable by the static tools under the returned name.
			var types = (JArray)Dbg.CallJson("list_types", new JObject {
				["module"] = loaded,
				["filter"] = "DbgTest.*",
			})["types"]!;
			Assert.Contains(types, t => (string?)t["name"] == "DbgTest.Program");
		}

		[DbgFact]
		public void An_unknown_module_is_reported() {
			var error = Dbg.CallExpectingError("dump_module", new JObject { ["module"] = "no-such-module.dll" });
			Assert.Contains("no loaded module", error);
		}
	}
}
