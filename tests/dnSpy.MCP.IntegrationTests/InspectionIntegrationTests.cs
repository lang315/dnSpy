using System;
using System.Diagnostics;
using System.IO;
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

		// dnSpy's autos provider is not implemented and answers "NYI" instead of failing, so the tool
		// turns that non-answer into an error rather than let a caller reason from it. If dnSpy ever
		// implements the provider the call succeeds and this fails — the signal to drop the workaround.
		[DbgFact]
		public void Autos_is_rejected_because_dnSpy_does_not_implement_it() {
			var error = Dbg.CallExpectingError("dbg_variables", new JObject { ["kind"] = "autos" });

			Assert.Contains("not implemented", error, StringComparison.OrdinalIgnoreCase);
			Assert.Contains("dbg_locals", error, StringComparison.Ordinal);
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

		// Program.Numbers is a flat int array, which only proves the tool can list children once. A
		// reference graph is the case an agent actually meets, and it needs the expression it hands
		// back to be usable as the next expression to expand.
		[DbgFact]
		public void Expanding_a_nested_object_walks_down_two_levels_of_the_graph() {
			PauseInsideInspect();

			var root = Dbg.CallJson("dbg_expand", new JObject { ["expression"] = "graph" });
			Assert.True((bool?)root["hasChildren"]);
			Assert.Contains("root", ChildValue(root, "Name"));
			Assert.Equal(1, Dbg.Number(ChildValue(root, "Value")));
			// The link the next expansion follows must be there and must not be null.
			Assert.DoesNotContain("null", ChildValue(root, "Child"), StringComparison.OrdinalIgnoreCase);

			var child = Dbg.CallJson("dbg_expand", new JObject { ["expression"] = "graph.Child" });
			Assert.Contains("child", ChildValue(child, "Name"));
			Assert.Equal(2, Dbg.Number(ChildValue(child, "Value")));

			var leaf = Dbg.CallJson("dbg_expand", new JObject { ["expression"] = "graph.Child.Child" });
			Assert.Contains("leaf", ChildValue(leaf, "Name"));
			Assert.Equal(3, Dbg.Number(ChildValue(leaf, "Value")));
		}

		// Every other value assertion in this suite is an int, so a formatter that only ever produced
		// numbers would pass all of them.
		[DbgFact]
		public void Non_numeric_locals_read_back_as_the_values_the_fixture_assigned() {
			PauseInsideInspect();

			// The formatter quotes and escapes strings, so assert on the content, not the spelling.
			var text = Dbg.CallArray("dbg_locals").First(l => (string?)l["name"] == "text");
			Assert.Contains("hello", (string?)text["value"] ?? "");

			// An array and a collection format as a summary rather than their contents, so those are
			// read back through the frame instead of parsed out of the summary text.
			Assert.Equal(3, Dbg.Number(Eval("letters.Length")));
			Assert.Contains("b", (string?)Eval("letters[1]") ?? "");

			Assert.Equal(3, Dbg.Number(Eval("list.Count")));
			Assert.Equal(7, Dbg.Number(Eval("list[0]")));
			Assert.Equal(9, Dbg.Number(Eval("list[2]")));
		}

		// max_children exists to keep a huge object from flooding the caller; untested, a tool that
		// ignored it would look identical on every object small enough to fit under the default.
		[DbgFact]
		public void Max_children_limits_how_many_children_are_returned() {
			var res = Dbg.CallJson("dbg_expand", new JObject {
				["expression"] = "DbgTest.Program.Numbers",
				["max_children"] = 2,
			});

			Assert.Equal(2, ((JArray)res["children"]!).Count);
			// The cap trims the returned list only; the caller still needs to see what it is missing.
			Assert.Equal(5, (int?)res["childCount"]);
		}

		// Return values are the one dbg_variables kind with no coverage. dnSpy fills them in after a
		// call completes under the debugger, but nothing in the engine promises it always will, so the
		// claim under test is that the tool answers instead of erroring — Dbg.CallArray throws on a
		// tool error, so reaching the assertions is itself the main assertion.
		[DbgFact]
		public void Return_values_are_reported_without_an_error_after_stepping_out_of_a_call() {
			// A return value only exists once a call has returned with the debugger watching, so leave
			// Add and land back in Main just after the call.
			Dbg.Call("dbg_step", new JObject { ["kind"] = "out" });

			var returns = Dbg.CallArray("dbg_variables", new JObject { ["kind"] = "returns" });

			// An empty list is a legitimate answer; only the shape of what is there is checked.
			Assert.All(returns, r => {
				Assert.False(string.IsNullOrWhiteSpace((string?)r["name"]));
				Assert.NotNull(r["value"]);
			});
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

		/// <summary>
		/// Repoints the session at Program.Inspect, paused on the line after every local has been
		/// assigned. The class-wide pause is inside Add, which holds nothing but ints; and a
		/// method-entry breakpoint on Inspect would stop at IL offset 0, where the locals under test
		/// are still uninitialised. Inspect is called on every pass of the fixture's loop, so the
		/// first hit is enough and no condition is needed.
		/// </summary>
		// No test here for expanding a BCL collection such as List<T>. Measured, and worth recording so
		// nobody spends the afternoon rediscovering it: the curated view always fails (the type has a
		// DebuggerTypeProxy and every child returns "Internal debugger error"), and the raw view works
		// only sometimes — the same call succeeds run-to-run depending on engine state. Asserting
		// either outcome would either enshrine a dnSpy bug or produce a flaky test. Use dbg_eval
		// (list.Count, list[0], list._items) instead, which is dependable.

		// The raw view is the only way to see a compiler-generated backing field, and hiding those is
		// exactly what the curated view is for — so the two must genuinely differ.
		[DbgFact]
		public void The_raw_view_shows_members_the_curated_view_hides() {
			PauseInsideInspect();

			var curated = (JArray)Dbg.CallJson("dbg_expand", new JObject { ["expression"] = "graph" })["children"]!;
			var raw = (JArray)Dbg.CallJson("dbg_expand", new JObject {
				["expression"] = "graph",
				["raw"] = true,
			})["children"]!;

			Assert.True(raw.Count > curated.Count, "the raw view should expose more than the curated one");
			Assert.DoesNotContain(curated, c => ((string?)c["name"])?.Contains("k__BackingField") == true);
			Assert.Contains(raw, c => ((string?)c["name"])?.Contains("k__BackingField") == true);
		}

		static void PauseInsideInspect() {
			Dbg.Reset();
			Dbg.Call("bp_add_line", new JObject {
				["module"] = Dbg.FixtureDll(),
				["method"] = "DbgTest.Program.Inspect",
				["line"] = FixtureLine("var total = seed + letters.Length + list.Count;"),
			});
			Dbg.Call("dbg_start", new JObject { ["path"] = Dbg.FixtureDll() });
			Dbg.WaitForBreak();

			// A pause occasionally lands with no readable stack, and every test here reads locals as its
			// first act. The breakpoint is hit once per loop pass, so rather than assert against a pause
			// that cannot answer, resume and take the next one — a few attempts is plenty when the
			// passes are 50 ms apart.
			for (int attempt = 0; attempt < 3 && !Dbg.HasFrames(); attempt++) {
				Dbg.Call("dbg_continue");
				Dbg.WaitUntil(() => Dbg.IsRunning, 5000);
				Dbg.WaitForBreak();
			}
			Assert.True(Dbg.HasFrames(), "paused inside Inspect but the stack never became readable");
		}

		static JToken? Eval(string expression) =>
			Dbg.CallJson("dbg_eval", new JObject { ["expression"] = expression })["value"];

		/// <summary>
		/// Reads one child out of a dbg_expand result by member name. An auto-property can surface
		/// either as the property or as its compiler-generated backing field depending on the
		/// formatter's settings, so match on the member name appearing in the child's name rather than
		/// on the two being equal — the test is about walking the graph, not about that setting.
		/// </summary>
		static string ChildValue(JObject expanded, string member) {
			var child = ((JArray)expanded["children"]!)
				.First(c => ((string?)c["name"])?.Contains(member, StringComparison.Ordinal) == true);
			return (string?)child["value"] ?? "";
		}

		/// <summary>
		/// Finds the 1-based source line of a marker, so line assertions survive edits above them.
		/// Deliberately a copy of the breakpoint suite's helper: the two suites are independent and
		/// neither should start failing because the other moved a private method.
		/// </summary>
		static int FixtureLine(string marker) {
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
}
