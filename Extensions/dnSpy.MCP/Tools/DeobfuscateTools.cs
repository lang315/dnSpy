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

			var result = HelperProcess.Run(host, argv, HostTimeoutMs, "the deobfuscation host",
				"the assembly may be very large or heavily packed");

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

		// Find dnSpy.MCP.DeobHost.exe next to this extension; it is published to <extension dir>\Deob\.
		static string LocateHost() =>
			HelperProcess.Locate(Path.Combine("Deob", "dnSpy.MCP.DeobHost.exe"),
				"The host is built from the de4dotEx source vendored in-tree under Extensions\\dnSpy.MCP\\Deob\\de4dotEx; restore that tree from git if it was deleted, then rebuild the dnSpy.MCP extension so the host is published.");
	}
}
