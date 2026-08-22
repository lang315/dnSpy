using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace dnSpy.MCP.IntegrationTests {
	/// <summary>
	/// diff_assemblies: compare two .NET modules and report the metadata differences by NAME and
	/// signature — never by token, since tokens are assigned per build and would report every member as
	/// changed. Like the other StaticTools it needs only the document service and dnlib, no debug
	/// session, so every test runs against modules on disk with nothing debugging.
	///
	/// Second module for the change-detection cases: this test project's own Newtonsoft.Json.dll, found
	/// via <c>typeof(JObject).Assembly.Location</c>. It is chosen because it is deterministic here —
	/// Newtonsoft.Json is a direct PackageReference of this project, so its DLL is always in the build
	/// output on disk (a path the same-machine dnSpy can load), it shares no application types with the
	/// dbgtest fixture, and it exposes rock-stable public type names (Newtonsoft.Json.JsonConvert). That
	/// lets the assertions key on specific names and counts>0 rather than brittle exact numbers.
	/// </summary>
	[Collection("dnSpy")]
	public class DiffAssembliesIntegrationTests : IDisposable {
		public DiffAssembliesIntegrationTests() => Dbg.Reset();
		public void Dispose() => Dbg.Reset();

		/// <summary>A different, always-on-disk managed assembly with well-known public type names.</summary>
		static string OtherModule => typeof(JObject).Assembly.Location;

		// Red before impl: diff_assemblies is not registered, so this is an unknown-tool error rather
		// than an (empty) diff. The identity case — a module diffed against itself — must report nothing
		// added or removed on any of the four lists, with zero counts and truncated=false.
		[DbgFact]
		public void A_module_diffed_against_itself_is_empty() {
			var res = Dbg.CallJson("diff_assemblies", new JObject {
				["old"] = Dbg.FixtureDll(),
				["new"] = Dbg.FixtureDll(),
			});

			Assert.Empty((JArray)res["addedTypes"]!);
			Assert.Empty((JArray)res["removedTypes"]!);
			// include_methods defaults to true, so the per-type method comparison ran and still found
			// nothing to report — the same module has an identical method set for every shared type.
			Assert.Empty((JArray)res["addedMethods"]!);
			Assert.Empty((JArray)res["removedMethods"]!);

			var counts = (JObject)res["counts"]!;
			Assert.Equal(0, (int)counts["addedTypes"]!);
			Assert.Equal(0, (int)counts["removedTypes"]!);
			Assert.Equal(0, (int)counts["addedMethods"]!);
			Assert.Equal(0, (int)counts["removedMethods"]!);
			Assert.False((bool)res["truncated"]!);
		}

		// Red before impl: there is no tool to produce an added/removed set to inspect. Diffing the
		// fixture (old) against a wholly different assembly (new) must place each side's exclusive types
		// on the correct list: Newtonsoft's JsonConvert only exists in 'new' (added), the fixture's
		// Program only in 'old' (removed).
		[DbgFact]
		public void Types_only_in_one_module_are_reported_on_the_right_side() {
			var res = Dbg.CallJson("diff_assemblies", new JObject {
				["old"] = Dbg.FixtureDll(),
				["new"] = OtherModule,
			});

			var added = ((JArray)res["addedTypes"]!).Select(t => (string?)t).ToArray();
			var removed = ((JArray)res["removedTypes"]!).Select(t => (string?)t).ToArray();

			// new-only vs old-only, keyed on stable names rather than exact counts.
			Assert.Contains("Newtonsoft.Json.JsonConvert", added);
			Assert.Contains("DbgTest.Program", removed);
			// ...and each name is only ever on its own side.
			Assert.DoesNotContain("DbgTest.Program", added);
			Assert.DoesNotContain("Newtonsoft.Json.JsonConvert", removed);

			var counts = (JObject)res["counts"]!;
			Assert.True((int)counts["addedTypes"]! > 0, "Newtonsoft adds many types over the fixture");
			Assert.True((int)counts["removedTypes"]! > 0, "the fixture's own types are absent from Newtonsoft");
		}

		// Red before impl: nothing honours include_methods, so there is no behaviour to gate. With
		// method diffing turned off the two method lists must stay empty even though the type sets
		// clearly differ — proving the flag suppresses the (more expensive) per-type method comparison
		// while the type diff still runs.
		[DbgFact]
		public void Include_methods_false_suppresses_the_method_diff() {
			var res = Dbg.CallJson("diff_assemblies", new JObject {
				["old"] = Dbg.FixtureDll(),
				["new"] = OtherModule,
				["include_methods"] = false,
			});

			// The type diff still ran (the modules genuinely differ)...
			Assert.True(((JArray)res["addedTypes"]!).Count > 0 || ((JArray)res["removedTypes"]!).Count > 0,
				"the two modules differ, so at least one type list must be non-empty");
			// ...but the method comparison was skipped.
			Assert.Empty((JArray)res["addedMethods"]!);
			Assert.Empty((JArray)res["removedMethods"]!);
			var counts = (JObject)res["counts"]!;
			Assert.Equal(0, (int)counts["addedMethods"]!);
			Assert.Equal(0, (int)counts["removedMethods"]!);
		}

		// Red before impl: an unknown tool errors the same way whatever the arguments, so it cannot name
		// the offending module. Each side is resolved independently, so a bad 'old' or a bad 'new' must
		// fail with a message that names exactly the one that could not be loaded (and resolving 'old'
		// first must not mask a bad 'new').
		[DbgFact]
		public void A_missing_module_is_reported_naming_which_one() {
			var oldMissing = Dbg.CallExpectingError("diff_assemblies", new JObject {
				["old"] = "no-such-old.dll",
				["new"] = Dbg.FixtureDll(),
			});
			Assert.Contains("no-such-old.dll", oldMissing);

			var newMissing = Dbg.CallExpectingError("diff_assemblies", new JObject {
				["old"] = Dbg.FixtureDll(),
				["new"] = "no-such-new.dll",
			});
			Assert.Contains("no-such-new.dll", newMissing);
		}
	}
}
