using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace dnSpy.MCP.IntegrationTests {
	/// <summary>
	/// Breakpoints against a real debug engine. "Bound" is the only claim worth asserting: a
	/// breakpoint that is accepted but never binds looks identical to a working one in bp_list, and
	/// that is exactly how the bare-module-name defect stayed hidden.
	/// </summary>
	[Collection("dnSpy")]
	public class BreakpointIntegrationTests : IDisposable {
		public BreakpointIntegrationTests() => Dbg.Reset();
		public void Dispose() => Dbg.Reset();

		// The regression that motivated this suite. ModuleId.Create(string) resolved a bare file name
		// against dnSpy's own working directory and then compared on the full path, so a breakpoint
		// named this way could never match the loaded module — it was accepted and silently never hit.
		[DbgFact]
		public void A_breakpoint_added_by_bare_module_name_binds_and_is_hit() {
			// Resolving by full path also opens the module in dnSpy, which is what makes the bare
			// file name resolvable afterwards.
			var token = Dbg.TokenOf("DbgTest.Program.Add");

			var added = Dbg.CallJson("bp_add", new JObject {
				["module"] = "dbgtest.dll",
				["token"] = token,
			});
			Assert.Equal(token, (string?)added["token"]);

			Dbg.Call("dbg_start", new JObject { ["path"] = Dbg.FixtureDll() });

			var paused = Dbg.WaitForBreak();
			Assert.True((bool?)paused["paused"]);

			var bp = Dbg.CallArray("bp_list").Single();
			Assert.True((int)bp["boundCount"]! >= 1, "breakpoint never bound to the loaded module");
			Assert.True(Dbg.HitCount(bp) >= 1, "breakpoint bound but was never hit");
		}

		[DbgFact]
		public void A_breakpoint_added_by_full_path_binds_and_is_hit() {
			Dbg.CallJson("bp_add", new JObject {
				["module"] = Dbg.FixtureDll(),
				["token"] = Dbg.TokenOf("DbgTest.Program.Add"),
			});

			Dbg.Call("dbg_start", new JObject { ["path"] = Dbg.FixtureDll() });
			Dbg.WaitForBreak();

			var bp = Dbg.CallArray("bp_list").Single();
			Assert.True((int)bp["boundCount"]! >= 1);
		}

		[DbgFact]
		public void An_unknown_module_is_reported_rather_than_silently_never_binding() {
			var error = Dbg.CallExpectingError("bp_add", new JObject {
				["module"] = "no-such-module.dll",
				["token"] = Dbg.TokenOf("DbgTest.Program.Add"),
			});

			Assert.Contains("no-such-module.dll", error);
		}

		[DbgFact]
		public void A_method_breakpoint_resolves_a_name_to_a_token_and_covers_every_overload() {
			var bps = Dbg.CallArray("bp_add_method", new JObject {
				["module"] = Dbg.FixtureDll(),
				["method"] = "DbgTest.Program.Add",
			});

			// Program.Add has a two-arg and a three-arg overload; both must get a breakpoint.
			Assert.Equal(2, bps.Count);
			Assert.All(bps, b => Assert.StartsWith("0x06", (string?)b["token"]));
		}

		// A nested type is Outer+Inner in metadata but an agent will write Outer.Inner.
		[DbgFact]
		public void A_method_breakpoint_resolves_a_nested_type_written_with_dots() {
			var bps = Dbg.CallArray("bp_add_method", new JObject {
				["module"] = Dbg.FixtureDll(),
				["method"] = "DbgTest.Outer.Inner.Ping",
			});

			Assert.Single(bps);
		}

		[DbgFact]
		public void A_line_breakpoint_binds_when_the_pdb_is_available() {
			var line = LineOf("var result = a + b;");

			var bp = Dbg.CallJson("bp_add_line", new JObject {
				["module"] = Dbg.FixtureDll(),
				["method"] = "DbgTest.Program.Add",
				["line"] = line,
			});

			Assert.NotNull(bp["token"]);

			Dbg.Call("dbg_start", new JObject { ["path"] = Dbg.FixtureDll() });
			Dbg.WaitForBreak();
			Assert.True((int)Dbg.CallArray("bp_list").Single()["boundCount"]! >= 1);
		}

		[DbgFact]
		public void A_hit_count_breakpoint_waits_for_the_configured_hit() {
			Dbg.Call("bp_add", new JObject {
				["module"] = Dbg.FixtureDll(),
				["token"] = Dbg.TokenOf("DbgTest.Program.Add"),
				["hit_count"] = 5,
			});

			Dbg.Call("dbg_start", new JObject { ["path"] = Dbg.FixtureDll() });
			Dbg.WaitForBreak();

			// The fixture calls Add once per iteration, so the first stop must be at or after the 5th.
			Assert.True(Dbg.HitCount(Dbg.CallArray("bp_list").Single()) >= 5);
		}

		[DbgFact]
		public void A_conditional_breakpoint_only_stops_when_the_condition_holds() {
			Dbg.Call("bp_add", new JObject {
				["module"] = Dbg.FixtureDll(),
				["token"] = Dbg.TokenOf("DbgTest.Program.Add"),
				["condition"] = "a == 7",
			});

			Dbg.Call("dbg_start", new JObject { ["path"] = Dbg.FixtureDll() });
			Dbg.WaitForBreak();

			var locals = Dbg.CallArray("dbg_locals");
			var a = locals.First(l => (string?)l["name"] == "a");
			Assert.Equal("7", (string?)a["value"]);
		}

		// bp_toggle is otherwise only proven by reading its own flag back, and a breakpoint that
		// reported enabled=false while still being armed in the runtime would look identical.
		[DbgFact]
		public void A_disabled_breakpoint_does_not_stop_the_program() {
			var bp = Dbg.CallJson("bp_add", new JObject {
				["module"] = Dbg.FixtureDll(),
				["token"] = Dbg.TokenOf("DbgTest.Program.Add"),
			});
			Dbg.Call("bp_toggle", new JObject { ["id"] = (int)bp["id"]!, ["enabled"] = false });

			Dbg.Call("dbg_start", new JObject { ["path"] = Dbg.FixtureDll() });
			Assert.True(Dbg.WaitUntil(() => Dbg.IsDebugging, 20000), "session never started");

			Assert.False(Dbg.WaitUntil(() => !Dbg.IsRunning, NeverStopsWindowMs),
				"the disabled breakpoint stopped the process");
			Assert.True(Dbg.IsRunning);
			// A disabled breakpoint is never armed in the runtime, so it cannot have accumulated hits.
			Assert.Equal(0, Dbg.HitCount(Dbg.CallArray("bp_list").Single()));
		}

		// Only the true case is covered above, and it cannot tell a working condition from an ignored
		// one: "a == 7" is reached anyway on the seventh pass, so the process would stop either way.
		[DbgFact]
		public void A_condition_that_can_never_hold_does_not_stop_the_program() {
			Dbg.Call("bp_add", new JObject {
				["module"] = Dbg.FixtureDll(),
				["token"] = Dbg.TokenOf("DbgTest.Program.Add"),
				// The fixture's loop only ever passes a = 1..1000, so this is false at every hit.
				["condition"] = "a == -1",
			});

			Dbg.Call("dbg_start", new JObject { ["path"] = Dbg.FixtureDll() });
			Assert.True(Dbg.WaitUntil(() => Dbg.IsDebugging, 20000), "session never started");

			Assert.False(Dbg.WaitUntil(() => !Dbg.IsRunning, NeverStopsWindowMs),
				"the process stopped even though the condition was never true");
			Assert.True(Dbg.IsRunning);
		}

		/// <summary>
		/// How long a "must not stop" test lets the fixture run before it accepts that the breakpoint
		/// is inert. The loop calls Add once per pass and sleeps 50 ms, so even allowing a couple of
		/// seconds for .NET startup and module load this covers well over a hundred passes through the
		/// breakpoint's method — a breakpoint that was going to fire has had every chance to. The wait
		/// is bounded on both ends: WaitUntil also gives up the moment the process does pause, so a
		/// failing run reports in a second rather than always costing the full window.
		/// </summary>
		const int NeverStopsWindowMs = 10000;

		[DbgFact]
		public void Breakpoints_can_be_listed_toggled_and_removed() {
			var bp = Dbg.CallJson("bp_add", new JObject {
				["module"] = Dbg.FixtureDll(),
				["token"] = Dbg.TokenOf("DbgTest.Program.Add"),
			});
			var id = (int)bp["id"]!;

			Assert.Single(Dbg.CallArray("bp_list"));

			Dbg.Call("bp_toggle", new JObject { ["id"] = id, ["enabled"] = false });
			Assert.False((bool?)Dbg.CallArray("bp_list").Single()["enabled"]);

			Dbg.Call("bp_remove", new JObject { ["id"] = id });
			Assert.Empty(Dbg.CallArray("bp_list"));
		}

		[DbgFact]
		public void Adding_the_same_breakpoint_twice_is_reported() {
			var args = new JObject { ["module"] = Dbg.FixtureDll(), ["token"] = Dbg.TokenOf("DbgTest.Program.Add") };
			Dbg.Call("bp_add", args);

			var error = Dbg.CallExpectingError("bp_add", (JObject)args.DeepClone());
			Assert.Contains("already exists", error);
		}

		[DbgFact]
		public void A_module_load_breakpoint_stops_the_process_when_the_module_loads() {
			Dbg.Call("mbp_add", new JObject { ["module_name"] = "dbgtest*" });

			Dbg.Call("dbg_start", new JObject { ["path"] = Dbg.FixtureDll() });
			var paused = Dbg.WaitForBreak();

			Assert.True((bool?)paused["paused"]);
			Assert.Single(Dbg.CallArray("mbp_list"));
		}

		/// <summary>Finds the 1-based source line of a marker, so line assertions survive edits above it.</summary>
		static int LineOf(string marker) {
			var source = Path.Combine(
				Environment.GetEnvironmentVariable("DNSPY_MCP_TEST_FIXTURE_SRC")
					?? throw new InvalidOperationException("DNSPY_MCP_TEST_FIXTURE_SRC is not set"),
				"Program.cs");
			var lines = File.ReadAllLines(source);
			for (int i = 0; i < lines.Length; i++) {
				if (lines[i].Contains(marker, StringComparison.Ordinal))
					return i + 1;
			}
			throw new InvalidOperationException($"marker not found in fixture source: {marker}");
		}
	}

	/// <summary>
	/// One dnSpy, one debug engine, one dispatcher thread — integration tests must not overlap.
	/// </summary>
	[CollectionDefinition("dnSpy", DisableParallelization = true)]
	public sealed class DnSpyCollection { }
}
