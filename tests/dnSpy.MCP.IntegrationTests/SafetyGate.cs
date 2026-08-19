using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using dnSpy.MCP.Tests; // Rpc/RpcResponse, shared with the Tier 1 project via a linked source file
using Newtonsoft.Json.Linq;

namespace dnSpy.MCP.IntegrationTests {
	/// <summary>
	/// Raised when the suite refuses to talk to a dnSpy that owns real user data. Deliberately not a
	/// <see cref="DbgToolException"/>: nothing in this suite may catch it and carry on.
	/// </summary>
	sealed class DbgUnsafeTargetException : Exception {
		public DbgUnsafeTargetException(string message) : base(message) { }
	}

	/// <summary>
	/// Refuses to let the suite run against a dnSpy that persists to the user's own profile.
	///
	/// The suite calls bp_remove all=true between tests, and dnSpy writes its breakpoints back into
	/// its settings file on a clean exit. Pointed at a normally-launched dnSpy that silently deletes
	/// the breakpoints someone set in their real work. It has already happened in this repo; the data
	/// survived only because dnSpy was killed hard and therefore never saved.
	///
	/// tests/run-integration.ps1 passes --settings-file at a temp path, but that lives in the caller:
	/// `dotnet test tests\dnSpy.MCP.IntegrationTests` bypasses it entirely. So the check belongs here,
	/// on the wire, where nothing can be ordered around it.
	/// </summary>
	static class SafetyGate {
		/// <summary>Read-only tool that reports which settings file the running dnSpy is using.</summary>
		const string InfoTool = "dnspy_info";

		static readonly object gate = new();
		static bool probed;
		static string? refusal; // null means "verified isolated"

		// The probe below issues its own request (a read-only dnspy_info), which now flows through the
		// same Rpc guard that calls Enforce. Suppress the guard for the duration of the probe on this
		// thread so it does not recurse into itself.
		[ThreadStatic] static bool inProbe;

		/// <summary>
		/// Blocks the caller unless the target dnSpy was verified isolated. The probe itself runs at
		/// most once per process; every later call is answered from the cached verdict, so this is
		/// cheap enough to sit in front of every single request.
		/// </summary>
		public static void Enforce() {
			if (inProbe)
				return;
			lock (gate) {
				if (!probed) {
					probed = true;
					inProbe = true;
					try { refusal = Probe(); }
					finally { inProbe = false; }
					if (refusal is not null) {
						// xUnit attributes the failure to whichever test ran first, which buries the
						// reason. Print it once where a human scanning the run output will see it.
						Console.Error.WriteLine();
						Console.Error.WriteLine(refusal);
						Console.Error.WriteLine();
					}
				}
			}
			if (refusal is not null)
				throw new DbgUnsafeTargetException(refusal);
		}

		/// <summary>Asks the server where it persists settings. Any doubt is answered with a refusal.</summary>
		static string? Probe() {
			// Nothing to protect when no endpoint was configured: every test skips itself anyway.
			if (!Dbg.Configured)
				return null;

			string? settingsFile;
			try {
				var res = Rpc.Post(Dbg.Url!, Rpc.CallTool(0, InfoTool), Dbg.Token);
				if (res.Json["error"] is JToken protocolError)
					return CannotVerify($"the server rejected {InfoTool}: {protocolError}");
				if (res.Json["result"] is not JObject result)
					return CannotVerify($"{InfoTool} returned no result");
				var text = (string?)result["content"]?[0]?["text"];
				if ((bool?)result["isError"] == true)
					return CannotVerify($"{InfoTool} reported: {text}");
				settingsFile = (string?)JObject.Parse(text ?? "{}")["settingsFile"];
			}
			catch (Exception ex) {
				// An old extension build has no dnspy_info at all. Fail closed: an interlock that
				// opens when it cannot see is not an interlock.
				return CannotVerify($"{InfoTool} could not be read ({ex.GetType().Name}: {ex.Message})");
			}

			if (string.IsNullOrWhiteSpace(settingsFile))
				return CannotVerify($"{InfoTool} did not report a settingsFile");
			// A relative path is dnSpy's, not ours: resolving it against this process's working
			// directory would be a guess, and a guess that lands in temp would open the gate.
			if (!Path.IsPathRooted(settingsFile!.Trim().Trim('"')))
				return CannotVerify($"{InfoTool} reported a relative settingsFile ({settingsFile})");

			return IsUnderTemp(settingsFile!) ? null : Refusal(settingsFile!);
		}

		static string Refusal(string settingsFile) => string.Join(Environment.NewLine,
			$"Refusing to run: dnSpy is using {settingsFile}.",
			"This suite clears all breakpoints and would delete real ones. Launch dnSpy via",
			"tests\\run-integration.ps1, which passes --settings-file pointing at a temp file.",
			$"(A settings file under {Path.GetTempPath()} is what this check expects to see.)");

		static string CannotVerify(string reason) => string.Join(Environment.NewLine,
			$"Refusing to run: cannot confirm which settings file dnSpy is using - {reason}.",
			"This suite clears all breakpoints and would delete real ones if dnSpy is running on",
			"your own profile. Launch dnSpy via tests\\run-integration.ps1, which passes",
			"--settings-file pointing at a temp file, and make sure the extension is current.");

		// ---- path comparison ----

		static bool IsUnderTemp(string settingsFile) {
			string file;
			try {
				file = Canonicalize(settingsFile);
			}
			catch (Exception) {
				return false; // unparseable path: treat as not isolated
			}

			foreach (var root in TempRoots()) {
				string prefix;
				try {
					prefix = Canonicalize(root) + Path.DirectorySeparatorChar;
				}
				catch (Exception) {
					continue;
				}
				if (file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
					return true;
			}
			return false;
		}

		/// <summary>
		/// TEMP/TMP and GetTempPath usually agree, but the runner and dnSpy can be started from
		/// shells with different values; accepting any of them avoids a false refusal.
		/// </summary>
		static IEnumerable<string> TempRoots() {
			yield return Path.GetTempPath();
			foreach (var name in new[] { "TEMP", "TMP" }) {
				var value = Environment.GetEnvironmentVariable(name);
				if (!string.IsNullOrWhiteSpace(value))
					yield return value!;
			}
		}

		/// <summary>
		/// Normalises a path far enough that two spellings of the same file compare equal: full path,
		/// long (non-8.3) form, no trailing separator. Comparison is then ordinal-ignore-case.
		/// </summary>
		static string Canonicalize(string path) =>
			ExpandShortName(Path.GetFullPath(path.Trim().Trim('"')))
				.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

		/// <summary>
		/// Expands 8.3 short names. Path.GetFullPath does not do this, so %TEMP% reported as
		/// C:\Users\LANG31~1.DES\AppData\Local\Temp and the server's
		/// C:\Users\lang315.DESKTOP-UNVOE92\AppData\Local\Temp\... would look like different trees and
		/// the gate would refuse a perfectly isolated run.
		/// </summary>
		static string ExpandShortName(string path) {
			if (TryGetLongPathName(path) is string expanded)
				return expanded;
			// GetLongPathName only resolves paths that exist, and dnSpy may not have written its
			// settings file yet. Expand the deepest ancestor that does exist and re-attach the rest.
			var parent = Path.GetDirectoryName(path);
			if (string.IsNullOrEmpty(parent))
				return path;
			var leaf = path.Substring(parent!.Length)
				.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			return leaf.Length == 0 ? path : Path.Combine(ExpandShortName(parent!), leaf);
		}

		static string? TryGetLongPathName(string path) {
			var buffer = new StringBuilder(260);
			var length = GetLongPathNameW(path, buffer, (uint)buffer.Capacity);
			if (length > buffer.Capacity) {
				buffer = new StringBuilder((int)length);
				length = GetLongPathNameW(path, buffer, (uint)buffer.Capacity);
			}
			return length == 0 ? null : buffer.ToString();
		}

		[DllImport("kernel32.dll", EntryPoint = "GetLongPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
		static extern uint GetLongPathNameW(string lpszShortPath, StringBuilder lpszLongPath, uint cchBuffer);
	}
}
