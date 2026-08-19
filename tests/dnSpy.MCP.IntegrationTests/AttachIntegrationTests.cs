using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using Xunit;

namespace dnSpy.MCP.IntegrationTests {
	/// <summary>
	/// Attaching to a process the suite started for itself. Everything else here launches the
	/// debuggee through dnSpy, so none of it covers dbg_attach: attaching reaches a process that is
	/// already running, whose modules are already loaded and whose main loop is already underway.
	/// </summary>
	[Collection("dnSpy")]
	public class AttachIntegrationTests : IDisposable {
		/// <summary>Printed by the fixture once its Main is running; see tests/fixture/dbgtest.</summary>
		const string ReadyLine = "dbgtest ready";
		const int StartTimeoutMs = 30000;
		const int AttachTimeoutMs = 30000;
		const int KillTimeoutMs = 15000;

		/// <summary>
		/// The fixture this test started, or null if it started none. Deliberately not disposed until
		/// it has been killed: an open process handle stops Windows recycling the pid, so the kill in
		/// <see cref="KillFixture"/> cannot land on an unrelated process that inherited it.
		/// </summary>
		Process? fixture;

		public AttachIntegrationTests() => Dbg.Reset();

		public void Dispose() {
			// Reset first: it resumes and stops the session, so the debuggee is no longer stopped by
			// dnSpy when it dies — killing a process dnSpy still has paused is the shape that once
			// took the whole app down (see the note on Dbg.Reset).
			//
			// The kill sits in a finally because Reset is exactly what cannot be trusted here: a dead
			// dnSpy, a refused safety gate or a session that will not stop all throw out of it, and a
			// debuggee that outlives the run holds a file lock on the fixture directory that poisons
			// every later build and launch. dbg_stop is not sufficient either — an attach session may
			// detach and leave the process running, which is the normal outcome, not an error.
			try {
				Dbg.Reset();
			}
			finally {
				KillFixture();
			}
		}

		[DbgFact]
		public void Attaching_by_pid_gives_a_usable_session() {
			var pid = StartFixture();

			// Resolved before attaching on purpose: TokenOf reads the token by setting a breakpoint on
			// Add and clearing it again, and the fixture calls Add every few milliseconds. Run inside
			// the session, that lookup would stop the process at a breakpoint the test never asked for
			// and the pause below would be the wrong one.
			var addToken = Dbg.TokenOf("DbgTest.Program.Add");

			Dbg.Call("dbg_attach", new JObject { ["pid"] = pid });

			Assert.True(Dbg.WaitUntil(() => IsDebugged(pid), AttachTimeoutMs),
				$"dnSpy never reported pid {pid} as a debugged process");

			// An attach that was merely accepted would get this far. Arming a breakpoint against the
			// already-loaded module, hitting it, and walking the stack is what proves the session is
			// real: all three go through the attached engine.
			Dbg.Call("bp_add", new JObject {
				["module"] = Dbg.FixtureDll(),
				["token"] = addToken,
			});
			Dbg.WaitForBreak();

			var frames = (JArray)Dbg.CallJson("dbg_callstack")["frames"]!;
			Assert.Contains("Add", (string?)frames[0]["frame"]);
			Assert.Contains("Main", string.Join(" ", frames.Select(f => (string?)f["frame"])));
		}

		[DbgFact]
		public void Attaching_by_a_wildcard_process_name_finds_the_same_process() {
			var pid = StartFixture();

			// dbg_attach takes the first match, so a dbgtest left behind by an earlier run would make
			// it attach to the wrong process and turn the assertion below into a coin toss. Name that
			// condition here rather than let it surface as an unrelated-looking failure.
			var strays = Dbg.CallArray("dbg_list_attachable", new JObject { ["name"] = "dbgtest*" })
				.Select(p => (int?)p["pid"])
				.Where(p => p != pid)
				.ToArray();
			Assert.True(strays.Length == 0,
				$"another dbgtest process is running (pid {string.Join(", ", strays)}); kill it and re-run");

			var text = Dbg.Call("dbg_attach", new JObject { ["name"] = "dbgtest*" });

			Assert.Contains($"pid {pid}", text, StringComparison.Ordinal);
			Assert.True(Dbg.WaitUntil(() => IsDebugged(pid), AttachTimeoutMs),
				$"dnSpy never reported pid {pid} as a debugged process");
		}

		[DbgFact]
		public void Attaching_to_a_pid_that_does_not_exist_is_an_error() {
			var error = Dbg.CallExpectingError("dbg_attach", new JObject { ["pid"] = UnusedPid() });

			Assert.Contains("attachable", error, StringComparison.OrdinalIgnoreCase);
			// A refused attach must leave the debugger untouched rather than half a session behind.
			Assert.False(Dbg.IsDebugging);
		}

		/// <summary>True once dnSpy reports the given pid among the processes it is debugging.</summary>
		static bool IsDebugged(int pid) =>
			((JArray)Dbg.Status()["processes"]!).Any(p => (int?)p["id"] == pid);

		/// <summary>
		/// Starts dbgtest.exe as an ordinary process — not through dnSpy — and returns its pid once
		/// the process is genuinely attachable. Two conditions, because they are different claims:
		/// the ready line means the CLR is up and Main is looping (so a breakpoint on Add will be
		/// hit), and the attachable listing means dnSpy can actually see the runtime to attach to.
		/// </summary>
		int StartFixture() {
			var exe = Dbg.FixtureExe();
			if (!File.Exists(exe))
				throw new InvalidOperationException($"fixture not built: {exe} does not exist");

			// Not disposed: the stdout callback may still fire after a timeout, and Set() on a
			// disposed event throws on a thread nobody is watching.
			var ready = new ManualResetEventSlim(false);
			var proc = new Process {
				StartInfo = new ProcessStartInfo(exe) {
					WorkingDirectory = Dbg.FixtureDir(),
					UseShellExecute = false,
					CreateNoWindow = true,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
				},
			};
			proc.OutputDataReceived += (_, e) => {
				if (e.Data is not null && e.Data.Contains(ReadyLine))
					ready.Set();
			};
			// Drained but ignored: an unread pipe fills up and blocks the fixture's next write, which
			// would stall the very loop the tests break into.
			proc.ErrorDataReceived += (_, e) => { };

			proc.Start();
			// Recorded before anything that can throw, so a failure below is still cleaned up.
			fixture = proc;
			proc.BeginOutputReadLine();
			proc.BeginErrorReadLine();

			if (!ready.Wait(StartTimeoutMs))
				throw new InvalidOperationException(
					$"the fixture never printed '{ReadyLine}' within {StartTimeoutMs}ms; it may have failed to start");

			var pid = proc.Id;
			Assert.True(
				Dbg.WaitUntil(() => Dbg.CallArray("dbg_list_attachable").Any(p => (int?)p["pid"] == pid),
					StartTimeoutMs),
				$"dnSpy never listed pid {pid} as attachable");
			return pid;
		}

		/// <summary>
		/// Kills the fixture this test started, on every path including a failed test. Reported as a
		/// failure if it survives: a live dbgtest locks the fixture directory, and a run that quietly
		/// leaked one would break the next run somewhere unrelated.
		/// </summary>
		void KillFixture() {
			var proc = fixture;
			fixture = null;
			if (proc is null)
				return;

			var pid = proc.Id; // read while the handle is still open
			bool exited;
			try {
				// entireProcessTree so a child the debuggee spawned cannot keep the lock alive.
				proc.Kill(entireProcessTree: true);
			}
			catch (InvalidOperationException) {
				// Already gone — dbg_stop terminated it, or it ran out of loop iterations.
			}
			catch (System.ComponentModel.Win32Exception) {
				// Already terminating, or access was refused because it is mid-exit.
			}
			try {
				exited = proc.WaitForExit(KillTimeoutMs);
			}
			finally {
				proc.Dispose();
			}

			if (!exited)
				throw new InvalidOperationException(
					$"the fixture process {pid} survived Kill(); it holds a lock on {Dbg.FixtureDir()} " +
					"and will break the next run — kill it by hand");
		}

		/// <summary>
		/// A pid that is not in use, so "attach to something that is not there" cannot accidentally
		/// name a real process. Windows allocates pids from well below this range, but the candidate
		/// is checked rather than assumed.
		/// </summary>
		static int UnusedPid() {
			for (int pid = 0x7FFFFFF0; pid > 0x7FFFFF00; pid -= 4) {
				try {
					using var existing = Process.GetProcessById(pid);
				}
				catch (ArgumentException) {
					return pid; // thrown only when no process has that id
				}
			}
			throw new InvalidOperationException("could not find an unused pid");
		}
	}
}
