using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace dnSpy.MCP.IntegrationTests {
	/// <summary>
	/// decompile_batch: decompile many types at once to a folder of files, with no debug session. Mirrors
	/// StaticIntegrationTests — every test runs against the fixture on disk with nothing debugging, which is
	/// what makes them safe and fast. The tool composes the same whole-type decompilation the 'decompile'
	/// tool uses with a type selector (type | namespace | filter) and writes one file per type.
	/// </summary>
	[Collection("dnSpy")]
	public class DecompileBatchIntegrationTests : IDisposable {
		readonly List<string> tempDirs = new();

		public DecompileBatchIntegrationTests() => Dbg.Reset();

		public void Dispose() {
			Dbg.Reset();
			// The batches write .cs/.il files under %TEMP%; drop them so a run does not litter. dnSpy holds the
			// *module* open (decompile loaded it), never our output files, so this normally succeeds.
			foreach (var dir in tempDirs) {
				try { Directory.Delete(dir, recursive: true); }
				catch { /* best effort */ }
			}
		}

		string Track(string dir) {
			tempDirs.Add(dir);
			return dir;
		}

		string NewOutDir() =>
			Track(Path.Combine(Path.GetTempPath(), "dnspymcp-batch-test-" + Guid.NewGuid().ToString("N").Substring(0, 8)));

		// Red before impl: decompile_batch does not exist, so the call is an unknown-tool error rather than a
		// folder of C#. Once implemented, a namespace select must write a real, readable file per type.
		[DbgFact]
		public void A_namespace_is_decompiled_to_a_folder_of_files() {
			// out_dir omitted, so the tool must pick a fresh temp dir and report it — exercises the default.
			var res = Dbg.CallJson("decompile_batch", new JObject {
				["module"] = Dbg.FixtureDll(),
				["namespace"] = "DbgTest",
			});
			Track((string)res["outDir"]!);

			Assert.Equal("cs", (string?)res["format"]);
			Assert.True((int)res["count"]! > 0, "the DbgTest namespace has types to decompile");

			// The Program type is among the written files, tagged with its TypeDef token, and the file on disk
			// holds real decompiled C#: a whole-type decompile includes every member, so both the class
			// declaration and Add's signature are present.
			var written = (JArray)res["written"]!;
			var program = written.FirstOrDefault(w => (string?)w["type"] == "DbgTest.Program");
			Assert.NotNull(program);
			Assert.StartsWith("0x02", (string?)program!["token"]); // TypeDef tokens start 0x02
			var file = (string)program["file"]!;
			Assert.EndsWith(".cs", file);
			Assert.True(File.Exists(file), $"decompile_batch reported {file} but it is missing on disk");
			var src = File.ReadAllText(file);
			Assert.Contains("class Program", src);
			Assert.Contains("int Add(int a, int b)", src);
		}

		// Red before impl: no tool means no IL export at all. format=il must write .il files whose content is
		// disassembly (a .method directive), not C#.
		[DbgFact]
		public void Il_format_writes_il_files() {
			var outDir = NewOutDir();
			var res = Dbg.CallJson("decompile_batch", new JObject {
				["module"] = Dbg.FixtureDll(),
				["type"] = "DbgTest.Program", // exact select => exactly this one type
				["format"] = "il",
				["out_dir"] = outDir,
			});

			Assert.Equal("il", (string?)res["format"]);
			Assert.Equal(1, (int)res["count"]!);
			var file = (string)((JArray)res["written"]!)[0]!["file"]!;
			Assert.EndsWith(".il", file);
			var il = File.ReadAllText(file);
			Assert.Contains(".method", il);              // IL, not C#
			Assert.DoesNotContain("int Add(int a, int b)", il);
		}

		// Red before impl: no tool, so no selection to subset. A wildcard filter must match a subset of the
		// module's types (like list_types' filter), pulling in the greeters but not Program.
		[DbgFact]
		public void A_wildcard_filter_selects_a_subset() {
			var outDir = NewOutDir();
			var res = Dbg.CallJson("decompile_batch", new JObject {
				["module"] = Dbg.FixtureDll(),
				["filter"] = "DbgTest.*Greeter*",
				["out_dir"] = outDir,
			});

			var written = (JArray)res["written"]!;
			// IGreeter, EnglishGreeter and FrenchGreeter all match; Program and Node do not.
			Assert.True((int)res["count"]! >= 2, "the *Greeter* filter should match several types");
			Assert.Equal((int)res["count"]!, written.Count); // small batch => the whole list is reported
			Assert.Contains(written, w => (string?)w["type"] == "DbgTest.EnglishGreeter");
			Assert.Contains(written, w => (string?)w["type"] == "DbgTest.FrenchGreeter");
			Assert.DoesNotContain(written, w => (string?)w["type"] == "DbgTest.Program");
			Assert.False((bool)res["truncated"]!, "nothing was truncated without a max");
			Assert.Equal(written.Count, Directory.GetFiles(outDir).Length); // one file on disk per type
		}

		// Red before impl: no tool, no max, no truncation flag. max=1 must cap the batch at one file and set
		// truncated=true because more types matched than were written.
		[DbgFact]
		public void Max_truncates_and_writes_exactly_one_file() {
			var outDir = NewOutDir();
			var res = Dbg.CallJson("decompile_batch", new JObject {
				["module"] = Dbg.FixtureDll(),
				["filter"] = "DbgTest.*Greeter*", // matches several, so max=1 genuinely truncates
				["max"] = 1,
				["out_dir"] = outDir,
			});

			Assert.Equal(1, (int)res["count"]!);
			Assert.True((bool)res["truncated"]!, "more types matched than max=1, so truncated must be set");
			Assert.Single((JArray)res["written"]!);
			Assert.Single(Directory.GetFiles(outDir)); // exactly one file on disk
		}

		// Red before impl: no tool. A selector that matches nothing must be a clean count-0 result, not an
		// error — an empty result is a valid answer, not a failure.
		[DbgFact]
		public void No_matches_returns_count_zero_cleanly() {
			var outDir = NewOutDir();
			var res = Dbg.CallJson("decompile_batch", new JObject {
				["module"] = Dbg.FixtureDll(),
				["filter"] = "DbgTest.NoSuchTypeZzz*",
				["out_dir"] = outDir,
			});

			Assert.Equal(0, (int)res["count"]!);
			Assert.False((bool)res["truncated"]!);
			Assert.Empty((JArray)res["written"]!);
			Assert.Empty(Directory.GetFiles(outDir)); // nothing written
		}

		// Red before impl: no tool. A module that cannot be resolved must surface as a clean tool error that
		// names the module, mirroring the other static tools (An_unknown_module_is_reported).
		[DbgFact]
		public void A_missing_module_errors_cleanly() {
			var error = Dbg.CallExpectingError("decompile_batch", new JObject {
				["module"] = "no-such.dll",
				["namespace"] = "DbgTest",
			});
			Assert.Contains("no-such.dll", error);
		}
	}
}
