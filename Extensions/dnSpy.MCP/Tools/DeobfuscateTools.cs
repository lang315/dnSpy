/*
    Copyright (C) 2014-2019 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using static dnSpy.MCP.Tools.JsonUtils;

namespace dnSpy.MCP.Tools {
	/// <summary>
	/// The <c>deobfuscate</c> tool: produces a cleaned COPY of an obfuscated .NET assembly on disk by
	/// running de4dot (de4dotEx) in a separate, locked-down child process (<c>dnSpy.MCP.DeobHost.exe</c>),
	/// then hands the caller the path to that copy so the existing static tools (decompile, list_types,
	/// find_references, …) can read it.
	///
	/// Why out-of-process: de4dot carries its own dnlib 4.5 and mutates whole modules; doing that in-proc
	/// would collide with the dnlib identity dnSpy's engine and the other tools rely on. The host reads an
	/// input path and writes an output path entirely in its own process — a clean file-in/file-out barrier
	/// that also contains anything de4dot does. The output is only ever used as a READ-ONLY input to the
	/// static analysis tools; it is never loaded into or re-injected into any live debug session.
	///
	/// Locked down (v1): renaming is OFF, control-flow deobfuscation is ON, and string decryption is STATIC
	/// (the static path does not execute the target's code) or NONE. Dynamic string decryption
	/// (delegate/emulate), which runs the assembly's own decrypter, is refused here and in the host.
	/// </summary>
	sealed class DeobfuscateTools {
		// A whole-module rewrite of a large packed assembly can take a while; the host is killed if it
		// overruns. Matches the "generous, bounded" stance of the heap helper timeout.
		const int HostTimeoutMs = 120_000;

		public IEnumerable<ToolDef> Create() {
			yield return new ToolDef("deobfuscate",
				"Produce a DEOBFUSCATED COPY of a .NET assembly on disk for static analysis, using de4dot (de4dotEx) in a separate, locked-down process. Reads 'module' (an input path) and writes a cleaned copy to 'save_path' (or a temp file); returns the output path so you can then point decompile / list_types / find_references / etc. at it. This is the recommended first step when an assembly is packed or its control flow/strings are obfuscated and the normal static tools return junk. " +
				"Locked down for safety: symbol RENAMING IS OFF (names are not invented), control-flow deobfuscation is ON, and string decryption is STATIC only — the static decrypter is reconstructed WITHOUT executing the target's code. Dynamic string decryption (which would run the assembly's own code) is disabled: string_decrypt='dynamic' is rejected. The output is a cleaned copy used only as READ-ONLY input to the other static tools; it is never injected into a running process. " +
				"Auto-detects the obfuscator by default (ConfuserEx, .NET Reactor, Babel, SmartAssembly, Dotfuscator, Eazfuscator, CryptoObfuscator and ~20 more); pass 'obfuscator' to force a type. If no known obfuscator is detected the tool still succeeds, returning a re-saved readable copy with a note. Writes a file, so this tool is not read-only.",
				Schema.Object(
					("module", Schema.Str("Path to the input .NET assembly (EXE/DLL) to deobfuscate. Read only; never modified in place."), true),
					("save_path", Schema.Str("Optional output path for the cleaned copy. Default: a '<name>-cleaned.<ext>' file in the temp directory."), false),
					("obfuscator", Schema.Str("Optional forced de4dot obfuscator type hint (e.g. 'crx'=ConfuserEx, 'dr4'=.NET Reactor v4, 'bl'=Babel, 'sa'=SmartAssembly). Omit to auto-detect."), false),
					("string_decrypt", Schema.Str("String decryption mode: 'static' (default; reconstructs the decrypter without running target code) or 'none'. 'dynamic'/'delegate'/'emulate' are rejected — they would execute the target's code."), false)),
				Deobfuscate, readOnly: false, destructive: false);
		}

		string Deobfuscate(JObject args) {
			var module = (string?)args["module"] ?? throw new ArgumentException("'module' is required");
			module = module.Trim();
			if (!File.Exists(module))
				throw new ArgumentException($"module not found: {module}");

			var savePath = ((string?)args["save_path"])?.Trim();
			var obfuscator = ((string?)args["obfuscator"])?.Trim();
			var stringDecrypt = ((string?)args["string_decrypt"])?.Trim().ToLowerInvariant() ?? "static";

			// Reject the code-executing modes up front (the host refuses them too — defense in depth).
			switch (stringDecrypt) {
			case "static":
			case "none":
				break;
			case "dynamic":
			case "delegate":
			case "emulate":
				throw new ArgumentException("dynamic string decryption runs the target's code and is disabled in this build; use string_decrypt='static' (no target code executed) or 'none'");
			default:
				throw new ArgumentException($"invalid string_decrypt '{stringDecrypt}'; expected 'static' or 'none'");
			}

			var host = LocateHost();

			var argv = new List<string> { "--in", module, "--string-decrypt", stringDecrypt };
			if (!string.IsNullOrEmpty(savePath)) {
				argv.Add("--out");
				argv.Add(savePath!);
			}
			if (!string.IsNullOrEmpty(obfuscator)) {
				argv.Add("--obfuscator");
				argv.Add(obfuscator!);
			}

			var (exitCode, stdout, stderr) = Spawn(host, argv);

			JObject result;
			try {
				result = JObject.Parse(stdout);
			}
			catch (Exception ex) {
				var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
				throw new InvalidOperationException($"the deobfuscation host did not return valid JSON (exit {exitCode}): {TrimText(detail)} [{ex.Message}]");
			}

			if ((bool?)result["ok"] != true) {
				var err = (string?)result["error"] ?? "deobfuscation failed";
				throw new InvalidOperationException(err);
			}

			// Reshape the host's payload into the tool's public result (drop internal fields like exitCode).
			var shaped = new JObject {
				["input"] = result["input"],
				["savedTo"] = result["savedTo"],
				["bytes"] = result["bytes"],
				["detectedObfuscator"] = result["detectedObfuscator"],
				["transformsApplied"] = result["transformsApplied"],
				["renamed"] = false,
				["stringDecrypt"] = result["stringDecrypt"],
				["note"] = result["note"],
			};
			return Json(shaped);
		}

		// Find dnSpy.MCP.DeobHost.exe next to this extension. It is published to <extension dir>\Deob\;
		// probe that first, then one directory up (covers the build.ps1 layout where the extension DLL and
		// the Deob folder move together) and AppContext.BaseDirectory as a fallback.
		static string LocateHost() {
			const string exeName = "dnSpy.MCP.DeobHost.exe";

			var bases = new List<string>();
			var loc = Assembly.GetExecutingAssembly().Location;
			if (!string.IsNullOrEmpty(loc)) {
				var dir = Path.GetDirectoryName(loc);
				if (!string.IsNullOrEmpty(dir)) {
					bases.Add(dir!);
					var parent = Path.GetDirectoryName(dir);
					if (!string.IsNullOrEmpty(parent))
						bases.Add(parent!);
				}
			}
			var baseDir = AppContext.BaseDirectory;
			if (!string.IsNullOrEmpty(baseDir))
				bases.Add(baseDir);

			foreach (var b in bases) {
				var candidate = Path.Combine(b, "Deob", exeName);
				if (File.Exists(candidate))
					return candidate;
			}

			var expected = bases.Count > 0
				? Path.Combine(bases[0], "Deob", exeName)
				: Path.Combine("Deob", exeName);
			throw new InvalidOperationException(
				$"the deobfuscation host ({exeName}) was not found next to the extension (expected under Deob\\). " +
				"Ensure the de4dotEx submodule is initialised (git submodule update --init --recursive) and rebuild the dnSpy.MCP extension so the host is published. " +
				$"Expected path: {expected}");
		}

		// Run the host with stdout/stderr redirected and no window, on the calling (request) thread. stdout
		// is read to completion; the process is killed if it overruns the timeout.
		(int exitCode, string stdout, string stderr) Spawn(string exe, List<string> argv) {
			var psi = new ProcessStartInfo {
				FileName = exe,
				Arguments = BuildArguments(argv),
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
				WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory,
				StandardOutputEncoding = Encoding.UTF8,
				StandardErrorEncoding = Encoding.UTF8,
			};

			using var proc = new Process { StartInfo = psi };
			try {
				proc.Start();
			}
			catch (Exception ex) {
				throw new InvalidOperationException($"could not start the deobfuscation host ({exe}): {ex.Message}");
			}

			var outTask = proc.StandardOutput.ReadToEndAsync();
			var errTask = proc.StandardError.ReadToEndAsync();

			if (!proc.WaitForExit(HostTimeoutMs)) {
				try { proc.Kill(); } catch { /* already gone */ }
				throw new InvalidOperationException($"the deobfuscation host did not finish within {HostTimeoutMs / 1000} s and was terminated (the assembly may be very large or heavily packed)");
			}
			proc.WaitForExit();

			return (proc.ExitCode, SafeResult(outTask), SafeResult(errTask));
		}

		static string SafeResult(Task<string> task) {
			try {
				return task.GetAwaiter().GetResult() ?? string.Empty;
			}
			catch {
				return string.Empty;
			}
		}

		// Quote arguments per the Windows CommandLineToArgvW rules so a path with spaces, quotes or trailing
		// backslashes round-trips. (net48 has no ProcessStartInfo.ArgumentList.)
		static string BuildArguments(List<string> argv) {
			var sb = new StringBuilder();
			foreach (var a in argv) {
				if (sb.Length > 0)
					sb.Append(' ');
				AppendArgument(sb, a);
			}
			return sb.ToString();
		}

		static void AppendArgument(StringBuilder sb, string arg) {
			if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) {
				sb.Append(arg);
				return;
			}
			sb.Append('"');
			for (int i = 0; ; i++) {
				var backslashes = 0;
				while (i < arg.Length && arg[i] == '\\') {
					i++;
					backslashes++;
				}
				if (i == arg.Length) {
					sb.Append('\\', backslashes * 2);
					break;
				}
				if (arg[i] == '"') {
					sb.Append('\\', backslashes * 2 + 1);
					sb.Append('"');
				}
				else {
					sb.Append('\\', backslashes);
					sb.Append(arg[i]);
				}
			}
			sb.Append('"');
		}

		static string TrimText(string s) {
			s = s.Trim();
			return s.Length <= 500 ? s : s.Substring(0, 500) + "…";
		}
	}
}
