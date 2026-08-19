using System;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using Xunit;

namespace dnSpy.MCP.IntegrationTests {
	/// <summary>
	/// Break-on-thrown-exception against a real engine. The only existing coverage asserted that
	/// exc_break reports itself enabled, which a tool that changed nothing at all would also pass:
	/// these tests require the debuggee to genuinely stop, or to genuinely keep running.
	/// </summary>
	[Collection("dnSpy")]
	public class ExceptionIntegrationTests : IDisposable {
		public ExceptionIntegrationTests() {
			Dbg.Reset();
			// Exception settings live in dnSpy, not in the debug session, so Dbg.Reset() does not
			// clear them and a run that died mid-test leaves first-chance breaking armed.
			DisableBreakOnException();
		}

		public void Dispose() {
			// Before Reset, not after: Reset resumes the process in order to stop it, and a setting
			// still armed would pause the fixture again on its very next loop pass.
			DisableBreakOnException();
			Dbg.Reset();
		}

		/// <summary>The exception Program.Boom throws and catches on every pass of the fixture's loop.</summary>
		const string FixtureException = "System.InvalidOperationException";

		/// <summary>
		/// How long a "must not stop" test lets the fixture run before it accepts that nothing is
		/// armed. The loop throws once per pass and sleeps 50 ms, so even after a couple of seconds of
		/// .NET startup this covers well over a hundred throws — a setting that was going to stop the
		/// process has had every chance to. WaitUntil also gives up the moment the process does pause,
		/// so a failing run reports in a second rather than always costing the full window.
		/// </summary>
		const int NeverStopsWindowMs = 10000;

		static void SetBreakOnException(string exception, bool enabled) =>
			Dbg.Call("exc_break", new JObject { ["exception"] = exception, ["enabled"] = enabled });

		/// <summary>
		/// Clears both ids this class can arm. exc_break has no "reset", and its effect outlives the
		/// session, so an id left enabled would derail every later test in the suite: they would stop
		/// at Boom instead of at their own breakpoint, on a process they never expected to pause.
		/// </summary>
		static void DisableBreakOnException() {
			Dbg.TryCall("exc_break", new JObject { ["exception"] = FixtureException, ["enabled"] = false });
			Dbg.TryCall("exc_break", new JObject { ["exception"] = "all", ["enabled"] = false });
		}

		static void StartFixture() => Dbg.Call("dbg_start", new JObject { ["path"] = Dbg.FixtureDll() });

		[DbgFact]
		public void Breaking_on_a_thrown_exception_actually_pauses_the_process() {
			SetBreakOnException(FixtureException, true);

			StartFixture();
			var paused = Dbg.WaitForBreak();

			Assert.True((bool?)paused["paused"]);
			Assert.False(Dbg.IsRunning);
			// No breakpoint is set in this test, so the throw is the only thing that can have stopped
			// it — and it stopped where the throw happened.
			var frames = (JArray)Dbg.CallJson("dbg_callstack")["frames"]!;
			Assert.Contains("Boom", string.Join(" ", frames.Select(f => (string?)f["frame"])));
		}

		[DbgFact]
		public void A_first_chance_pause_describes_the_exception_through_dbg_variables() {
			SetBreakOnException(FixtureException, true);
			StartFixture();
			Dbg.WaitForBreak();

			var exceptions = Dbg.CallArray("dbg_variables", new JObject { ["kind"] = "exceptions" });

			Assert.NotEmpty(exceptions);
			var described = string.Join(" ", exceptions.Select(e => $"{(string?)e["name"]} {(string?)e["value"]}"));
			// Only the presence of the node is asserted, not the type. Measured: at a first-chance pause
			// dnSpy answers "$exception {ThreadAbortException}" whatever was actually thrown — the frame
			// is a native transition and the real type is not recovered. Asserting
			// InvalidOperationException here would be asserting a bug into place; the call stack test
			// above is what proves which throw stopped us.
			Assert.Contains("$exception", described, StringComparison.Ordinal);
		}

		/// <summary>
		/// Order matters: exc_break has to be armed before dbg_start, or the early throws happen while
		/// nothing is watching.
		///
		/// This asserts on WHEN the pause arrives, not on program state. Reading DbgTest.Program.Counter
		/// would say exactly which loop pass was caught, and that was the original plan — but an
		/// exception pause parks the thread on an unsafe point where the stack is transient: it reads
		/// back empty, then eighteen frames, then empty again between two consecutive calls, and eval is
		/// refused outright on the native transition frame. No amount of retrying makes that dependable.
		/// The fixture throws on its first loop pass a few hundred milliseconds in, so a pause this
		/// close to the start can only be an early throw; the sibling test above proves it is the
		/// fixture's throw and not something else.
		/// </summary>
		[DbgFact]
		public void Break_on_exception_armed_before_the_start_catches_an_early_throw() {
			SetBreakOnException(FixtureException, true);

			var sw = System.Diagnostics.Stopwatch.StartNew();
			StartFixture();
			Dbg.WaitForBreak();
			sw.Stop();

			Assert.True(sw.Elapsed.TotalSeconds < 20,
				$"the pause took {sw.Elapsed.TotalSeconds:F1}s, too late to be one of the first throws");
			Assert.False(Dbg.IsRunning);
		}

		[DbgFact(Skip = "The stack at an exception pause is transient — see the comment above; kept as a record.")]
		public void Reading_program_state_at_an_exception_pause_is_not_dependable() {
			var counter = Dbg.CallJson("dbg_eval", new JObject {
				["expression"] = "DbgTest.Program.Counter",
				["frame_index"] = FirstFixtureFrame(),
			});
			Assert.Equal(1, Dbg.Number(counter["value"]));
		}

		/// <summary>Index of the topmost frame that belongs to the fixture rather than to the runtime.</summary>
		static int FirstFixtureFrame() {
			// An exception pause parks the thread on an unsafe point, and the stack does not always
			// materialise at once — it comes back empty for a moment before the frames appear. Retry
			// rather than read the gap as "the fixture is not on the stack".
			JArray frames = new JArray();
			for (int attempt = 0; attempt < 20; attempt++) {
				frames = (JArray)Dbg.CallJson("dbg_callstack")["frames"]!;
				for (int i = 0; i < frames.Count; i++) {
					if (((string?)frames[i]["frame"])?.Contains("DbgTest.", StringComparison.Ordinal) == true)
						return i;
				}
				Thread.Sleep(250);
			}
			throw new InvalidOperationException(
				"no fixture frame on the stack at the exception pause: " +
				string.Join(" | ", frames.Select(f => (string?)f["frame"])));
		}

		/// <summary>
		/// 'all' is not "every exception", however the tool's description reads. It sets the
		/// category-wide id, which dnSpy's own exceptions window labels "&lt;All ... not in this
		/// list&gt;": DbgExceptionSettingsService.GetSettings prefers an entry matching the thrown
		/// type's name and only falls back to the category one, and DotNet.ex.xml ships a definition
		/// for System.InvalidOperationException (with stop-on-second-chance only). So the fixture's
		/// exception keeps its own settings and the process runs on.
		/// </summary>
		[DbgFact]
		public void Breaking_on_all_exceptions_does_not_cover_a_type_dnSpy_lists_by_name() {
			SetBreakOnException("all", true);

			StartFixture();
			Assert.True(Dbg.WaitUntil(() => Dbg.IsDebugging, 20000), "session never started");

			Assert.False(Dbg.WaitUntil(() => !Dbg.IsRunning, NeverStopsWindowMs),
				"the category-wide setting stopped on an exception that has its own definition");
			Assert.True(Dbg.IsRunning);
		}

		// The enable is not incidental: disabling an id that was never enabled would pass this test
		// even if the disable branch did nothing at all.
		[DbgFact]
		public void Disabling_break_on_exception_lets_the_process_keep_running() {
			SetBreakOnException(FixtureException, true);
			SetBreakOnException(FixtureException, false);

			StartFixture();
			Assert.True(Dbg.WaitUntil(() => Dbg.IsDebugging, 20000), "session never started");

			Assert.False(Dbg.WaitUntil(() => !Dbg.IsRunning, NeverStopsWindowMs),
				"the process stopped even though break-on-exception was disabled again");
			Assert.True(Dbg.IsRunning);
		}
	}
}
