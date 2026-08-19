using System;
using System.Diagnostics;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace dnSpy.MCP.IntegrationTests {
	/// <summary>
	/// Inspecting a genuinely paused process: call stack, locals, expression evaluation and the
	/// state-changing tools. Func-eval runs code inside the debuggee, so these only mean anything
	/// against a real engine.
	/// </summary>
	[Collection("dnSpy")]
	public class InspectionIntegrationTests : IDisposable {
		public InspectionIntegrationTests() {
			Dbg.Reset();
			PauseInsideAdd();
		}

		public void Dispose() => Dbg.Reset();

		/// <summary>Stops the fixture inside Program.Add with known locals (a == 7, b == 1).</summary>
		static void PauseInsideAdd() {
			Dbg.Call("bp_add", new JObject {
				["module"] = Dbg.FixtureDll(),
				["token"] = Dbg.TokenOf("DbgTest.Program.Add"),
				["condition"] = "a == 7",
			});
			Dbg.Call("dbg_start", new JObject { ["path"] = Dbg.FixtureDll() });
			Dbg.WaitForBreak();
		}

		[DbgFact]
		public void The_call_stack_names_the_paused_method_and_its_caller() {
			var stack = Dbg.CallJson("dbg_callstack");
			var frames = (JArray)stack["frames"]!;

			Assert.True(frames.Count >= 2, "expected Add and its caller Main");
			Assert.Contains("Add", (string?)frames[0]["frame"]);
			Assert.Contains("Main", string.Join(" ", frames.Select(f => (string?)f["frame"])));
		}

		[DbgFact]
		public void Locals_report_the_argument_values_the_fixture_was_called_with() {
			var locals = Dbg.CallArray("dbg_locals");

			Assert.Equal(7, Dbg.Number(locals.First(l => (string?)l["name"] == "a")["value"]));
			Assert.Equal(1, Dbg.Number(locals.First(l => (string?)l["name"] == "b")["value"]));
		}

		[DbgFact]
		public void An_arithmetic_expression_is_evaluated_in_the_frame() {
			var res = Dbg.CallJson("dbg_eval", new JObject { ["expression"] = "a + b" });

			Assert.Equal(8, Dbg.Number(res["value"]));
		}

		// Calling a method requires func-eval: the engine runs code inside the debuggee and returns.
		[DbgFact]
		public void A_method_call_expression_runs_inside_the_debuggee() {
			var res = Dbg.CallJson("dbg_eval", new JObject { ["expression"] = "System.Math.Max(a, b)" });

			Assert.Equal(7, Dbg.Number(res["value"]));
		}

		[DbgFact]
		public void Static_fields_are_listed() {
			var statics = Dbg.CallArray("dbg_variables", new JObject { ["kind"] = "statics" });

			Assert.NotEmpty(statics);
		}

		// dnSpy's autos provider is not implemented and answers "NYI". This is a dnSpy limitation, not
		// an extension defect — asserted so the suite notices if dnSpy ever starts supporting it.
		[DbgFact]
		public void Autos_is_still_unimplemented_by_dnSpy() {
			var autos = Dbg.CallArray("dbg_variables", new JObject { ["kind"] = "autos" });

			var text = autos.ToString();
			Assert.True(text.Contains("NYI"),
				"dnSpy now implements the autos provider — drop this expectation and assert real values instead");
		}

		[DbgFact]
		public void An_object_expression_can_be_expanded_into_its_children() {
			var res = Dbg.CallJson("dbg_expand", new JObject {
				["expression"] = "DbgTest.Program.Numbers",
			});

			Assert.True((bool?)res["hasChildren"]);
			Assert.Equal(5, (int?)res["childCount"]);
			Assert.Contains(10, ((JArray)res["children"]!).Select(c => Dbg.Number(c["value"])));
		}

		[DbgFact]
		public void A_variable_can_be_assigned_and_the_new_value_reads_back() {
			Dbg.Call("dbg_set_variable", new JObject { ["target"] = "b", ["value"] = "777" });

			var res = Dbg.CallJson("dbg_eval", new JObject { ["expression"] = "b" });
			Assert.Equal(777, Dbg.Number(res["value"]));
		}

		// Regression: a JIT-compiled frame reports DbgDotNetNativeCodeLocation, which does not derive
		// from DbgDotNetCodeLocation. Matching the concrete class made this fail on every real frame.
		[DbgFact]
		public void The_instruction_pointer_can_be_moved_within_the_method() {
			// The breakpoint pauses at offset 0, so move away first — otherwise "go back to 0x0" would
			// pass without the instruction pointer having moved at all.
			Dbg.Call("dbg_step", new JObject { ["kind"] = "over" });

			var before = Dbg.CallJson("dbg_callstack");
			var offsetBefore = (string?)((JArray)before["frames"]!)[0]["offset"];
			Assert.NotEqual("0x0", offsetBefore);

			Dbg.Call("dbg_set_next_statement", new JObject { ["il_offset"] = "0x0" });

			var after = Dbg.CallJson("dbg_callstack");
			var offsetAfter = (string?)((JArray)after["frames"]!)[0]["offset"];

			Assert.Equal("0x0", offsetAfter);
			Assert.NotEqual(offsetBefore, offsetAfter);
		}

		[DbgFact]
		public void Stepping_over_advances_the_paused_frame() {
			var before = (string?)((JArray)Dbg.CallJson("dbg_callstack")["frames"]!)[0]["offset"];

			Dbg.Call("dbg_step", new JObject { ["kind"] = "over" });

			var after = (string?)((JArray)Dbg.CallJson("dbg_callstack")["frames"]!)[0]["offset"];
			Assert.NotEqual(before, after);
		}

		// Regression: evaluation runs on the single debug-engine dispatcher thread, and the generic
		// 10s marshalling timeout used to abort any getter slower than that even though the engine
		// was working correctly.
		[DbgFact]
		public void An_evaluation_slower_than_ten_seconds_still_completes() {
			var sw = Stopwatch.StartNew();
			var res = Dbg.CallJson("dbg_eval", new JObject {
				["expression"] = "DbgTest.Slow.SlowProperty",
			});
			sw.Stop();

			Assert.Equal(123, Dbg.Number(res["value"]));
			Assert.True(sw.Elapsed.TotalSeconds > 10,
				"the fixture property should have blocked for ~15s; check the fixture, not the server");
		}

		[DbgFact]
		public void Threads_and_modules_of_the_paused_process_are_listed() {
			Assert.NotEmpty(Dbg.CallArray("dbg_threads"));

			var modules = Dbg.CallArray("dbg_modules");
			Assert.Contains(modules, m => ((string?)m["name"])?.Contains("dbgtest") == true);
		}
	}
}
