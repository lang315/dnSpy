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
using System.IO;
using System.Text;
using System.Text.Json;

namespace dnSpy.MCP.DeobHost {
	/// <summary>
	/// Locked-down, out-of-process de4dot host spawned by the dnSpy.MCP <c>deobfuscate</c> tool.
	///
	/// It reads an input assembly path and writes a cleaned COPY to an output path, entirely inside this
	/// child process (dnSpy's dnlib never touches de4dot's). The transform is deliberately restricted to
	/// the static, non-executing subset of de4dot:
	///   * renaming is OFF          (<c>--dont-rename</c>: RenameSymbols=false, RenamerFlags=0)
	///   * control-flow deob is ON  (de4dot's default; we never pass <c>--no-cflow-deob</c>)
	///   * string decryption is STATIC or NONE (<c>--default-strtyp static|none</c>)
	///
	/// The dynamic string decrypters (delegate/emulate) are refused: they are the only paths in
	/// ObfuscatedFile that build an AssemblyClient and run the target's own code. By never selecting them
	/// and never installing an AssemblyClientFactory, de4dot's AssemblyServer is never engaged, so no code
	/// from the analysed assembly is executed here.
	///
	/// Protocol: arguments in, one JSON object out on stdout.
	///   --in &lt;path&gt;              required input assembly
	///   --out &lt;path&gt;             optional output path (default: %TEMP%\&lt;name&gt;-cleaned&lt;ext&gt;)
	///   --obfuscator &lt;type&gt;      optional forced de4dot obfuscator type (-p), else auto-detect
	///   --string-decrypt static|none   default static; 'dynamic'/'delegate'/'emulate' are rejected
	/// </summary>
	static class Program {
		static int Main(string[] rawArgs) {
			// de4dot.cui.Program.Main calls Console.ReadKey() for "n00b" users (windir set, no PROMPT).
			// This host is always spawned head-less with redirected streams; set SHELL so IsN00bUser()
			// returns false and it never blocks waiting for a keypress.
			Environment.SetEnvironmentVariable("SHELL", "1");

			try {
				return Run(rawArgs);
			}
			catch (Exception ex) {
				// Any unexpected failure still leaves the caller a single parseable JSON object.
				WriteJson(new Dictionary<string, object?> {
					["ok"] = false,
					["error"] = "deobfuscation host failed: " + ex.Message,
				});
				return 1;
			}
		}

		static int Run(string[] rawArgs) {
			string? input = null, output = null, obfuscator = null;
			var stringDecrypt = "static";

			for (int i = 0; i < rawArgs.Length; i++) {
				switch (rawArgs[i]) {
				case "--in": input = Next(rawArgs, ref i); break;
				case "--out": output = Next(rawArgs, ref i); break;
				case "--obfuscator": obfuscator = Next(rawArgs, ref i); break;
				case "--string-decrypt": stringDecrypt = Next(rawArgs, ref i) ?? "static"; break;
				default: return Fail($"unknown argument '{rawArgs[i]}'");
				}
			}

			if (string.IsNullOrEmpty(input))
				return Fail("missing --in (input assembly path)");

			// The lockdown boundary: never let the target's code run. delegate/emulate/dynamic all build a
			// de4dot AssemblyClient that loads and executes the assembly's real string decrypter.
			switch (stringDecrypt) {
			case "static":
			case "none":
				break;
			case "dynamic":
			case "delegate":
			case "emulate":
				return Fail("dynamic string decryption runs the target's code and is disabled in this build; use string_decrypt='static' (no code executed) or 'none'");
			default:
				return Fail($"invalid string_decrypt '{stringDecrypt}'; expected 'static' or 'none'");
			}

			if (!File.Exists(input))
				return Fail("input assembly not found: " + input);

			if (string.IsNullOrEmpty(output)) {
				var name = Path.GetFileNameWithoutExtension(input);
				var ext = Path.GetExtension(input);
				output = Path.Combine(Path.GetTempPath(), name + "-cleaned" + ext);
			}
			// de4dot refuses to overwrite the input in place; guard against in==out early with a clear message.
			if (PathsEqual(input!, output!))
				return Fail("--out must differ from --in (de4dot writes a separate cleaned copy)");

			// Build the locked-down de4dot command line. Order matters: --dont-rename and --default-strtyp
			// are global and must precede the input file; -p (forced type) is also global.
			var argv = new List<string> {
				"--dont-rename",                    // RenameSymbols=false, RenamerFlags=0
				"--default-strtyp", stringDecrypt,  // static | none  (never delegate/emulate)
			};
			// control-flow deobfuscation is ON by de4dot default — we intentionally do NOT pass --no-cflow-deob.
			if (!string.IsNullOrEmpty(obfuscator)) {
				argv.Add("-p");
				argv.Add(obfuscator!);
			}
			argv.Add(input!);
			argv.Add("-o");
			argv.Add(output!);

			// Run de4dot with its console captured so this host emits only JSON on stdout.
			var log = new StringWriter();
			var savedOut = Console.Out;
			var savedErr = Console.Error;
			int exitCode;
			try {
				Console.SetOut(log);
				Console.SetError(log);
				exitCode = de4dot.cui.Program.Main(argv.ToArray());
			}
			finally {
				Console.SetOut(savedOut);
				Console.SetError(savedErr);
			}

			var logText = log.ToString();
			var detected = ParseDetectedObfuscator(logText);
			var wrote = File.Exists(output!);
			var bytes = wrote ? new FileInfo(output!).Length : 0L;

			if (exitCode != 0 || !wrote) {
				return Fail("de4dot did not produce an output file" +
					(detected is null ? "" : $" (detected: {detected})") +
					$"; exit={exitCode}. " + Tail(logText));
			}

			var isUnknown = detected is null ||
				detected.IndexOf("Unknown", StringComparison.OrdinalIgnoreCase) >= 0;

			var transforms = new List<string> { "control-flow" };
			if (stringDecrypt == "static")
				transforms.Add("string-decryption(static)");

			var note = isUnknown
				? "No known obfuscator detected; only generic static cleanup (control flow" +
					(stringDecrypt == "static" ? " + static strings" : "") + ") was applied. The output is a re-saved, readable copy."
				: $"Deobfuscated a copy with de4dot ({detected}). Renaming was left OFF and no target code was executed.";

			WriteJson(new Dictionary<string, object?> {
				["ok"] = true,
				["exitCode"] = exitCode,
				["input"] = input,
				["savedTo"] = output,
				["bytes"] = bytes,
				["detectedObfuscator"] = detected ?? "Unknown Obfuscator",
				["renamed"] = false,
				["stringDecrypt"] = stringDecrypt,
				["transformsApplied"] = transforms,
				["note"] = note,
			});
			return 0;
		}

		// de4dot (FilesDeobfuscator) logs "Detected <Name> (<filename>)" once it picks a deobfuscator.
		static string? ParseDetectedObfuscator(string log) {
			foreach (var raw in log.Split('\n')) {
				var line = raw.Trim();
				const string marker = "Detected ";
				var idx = line.IndexOf(marker, StringComparison.Ordinal);
				if (idx < 0)
					continue;
				var rest = line.Substring(idx + marker.Length);
				// Strip the trailing " (path)" that names the file.
				var paren = rest.LastIndexOf(" (", StringComparison.Ordinal);
				if (paren > 0)
					rest = rest.Substring(0, paren);
				rest = rest.Trim();
				if (rest.Length > 0)
					return rest;
			}
			return null;
		}

		static string? Next(string[] args, ref int i) => i + 1 < args.Length ? args[++i] : null;

		static bool PathsEqual(string a, string b) {
			try {
				return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
			}
			catch {
				return false;
			}
		}

		static string Tail(string s) {
			s = s.Trim();
			const int max = 600;
			return s.Length <= max ? s : "…" + s.Substring(s.Length - max);
		}

		static int Fail(string message) {
			WriteJson(new Dictionary<string, object?> {
				["ok"] = false,
				["error"] = message,
			});
			return 1;
		}

		static void WriteJson(Dictionary<string, object?> obj) {
			var json = JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = false });
			// A single line of UTF-8 JSON on stdout — the tool parses exactly this.
			var stdout = Console.OpenStandardOutput();
			var bytes = new UTF8Encoding(false).GetBytes(json);
			stdout.Write(bytes, 0, bytes.Length);
			stdout.Flush();
		}
	}
}
