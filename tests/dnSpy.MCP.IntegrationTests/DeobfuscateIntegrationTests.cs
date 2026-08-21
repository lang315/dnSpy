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
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace dnSpy.MCP.IntegrationTests {
	/// <summary>
	/// The <c>deobfuscate</c> tool spawns the out-of-process, locked-down de4dot host, which writes a
	/// cleaned COPY of an assembly to disk; the static tools then read that copy. No debug session is
	/// involved — it is a pure file-in/file-out transform, so every test here runs against the fixture on
	/// disk with nothing debugging.
	///
	/// MECHANISM test (red-before-impl): before this change there was no 'deobfuscate' tool, no Deob host,
	/// and no de4dotEx submodule, so <see cref="Deobfuscating_the_fixture_yields_a_readable_copy"/> failed
	/// at the tool call ("unknown tool"). We have no obfuscated fixture, so this proves the PLUMBING —
	/// spawn, file-in/file-out, JSON contract, and that the output is a valid, readable assembly — against
	/// the UNobfuscated fixture (de4dot detects no known obfuscator and re-saves a clean copy). A real
	/// ConfuserEx/Reactor sample is a documented follow-up, not covered here.
	/// </summary>
	[Collection("dnSpy")]
	public class DeobfuscateIntegrationTests : IDisposable {
		readonly List<string> tempOutputs = new();

		public DeobfuscateIntegrationTests() => Dbg.Reset();

		public void Dispose() {
			Dbg.Reset();
			// Cleaned copies go to %TEMP%; drop them so a run does not litter. dnSpy may still hold one open
			// (list_types/decompile opened it), so deletion is best-effort.
			foreach (var path in tempOutputs) {
				try { File.Delete(path); }
				catch { /* best effort */ }
			}
		}

		// The baseline that MUST pass: deobfuscate the fixture and prove the result is a real, parseable
		// assembly the static tools can read. This exercises the whole out-of-process path end to end.
		[DbgFact]
		public void Deobfuscating_the_fixture_yields_a_readable_copy() {
			var res = Dbg.CallJson("deobfuscate", new JObject {
				["module"] = Dbg.FixtureDll(),
			});

			var savedTo = (string?)res["savedTo"]
				?? throw new InvalidOperationException("deobfuscate returned no savedTo");
			tempOutputs.Add(savedTo);

			// The cleaned copy exists, is non-empty, and starts with the PE 'MZ' signature.
			Assert.True(File.Exists(savedTo), $"deobfuscate reported {savedTo} but the file is missing");
			Assert.True((long?)res["bytes"] > 0, "the cleaned copy is empty");
			Assert.Equal(new byte[] { 0x4D, 0x5A }, ReadHead(savedTo, 2)); // 'M','Z'

			// The locked-down contract is reported back: renaming OFF, static strings, control-flow applied.
			Assert.False((bool?)res["renamed"], "renaming must be OFF in this build");
			Assert.Equal("static", (string?)res["stringDecrypt"]);
			var transforms = ((JArray?)res["transformsApplied"])?.Select(t => (string?)t).ToArray() ?? Array.Empty<string?>();
			Assert.Contains("control-flow", transforms);
			// An unobfuscated input detects no known obfuscator; the tool still succeeds with a note.
			Assert.False(string.IsNullOrWhiteSpace((string?)res["detectedObfuscator"]));
			Assert.False(string.IsNullOrWhiteSpace((string?)res["note"]));

			// The output is a valid, readable assembly: list_types, pointed at the cleaned copy, finds the
			// fixture's own type. This is the real proof that the host produced a loadable module.
			var types = (JArray)Dbg.CallJson("list_types", new JObject {
				["module"] = savedTo,
				["filter"] = "DbgTest.Program",
			})["types"]!;
			Assert.Contains(types, t => (string?)t["name"] == "DbgTest.Program");
		}

		// An explicit save_path is honored, and the copy written there is likewise readable.
		[DbgFact]
		public void An_explicit_save_path_is_honored() {
			var outPath = Path.Combine(Path.GetTempPath(), $"dbgtest-deob-{Guid.NewGuid():N}.dll");
			tempOutputs.Add(outPath);

			var res = Dbg.CallJson("deobfuscate", new JObject {
				["module"] = Dbg.FixtureDll(),
				["save_path"] = outPath,
			});

			Assert.Equal(outPath, (string?)res["savedTo"]);
			Assert.True(File.Exists(outPath), "the cleaned copy was not written to save_path");

			var types = (JArray)Dbg.CallJson("list_types", new JObject {
				["module"] = outPath,
				["filter"] = "DbgTest.Program",
			})["types"]!;
			Assert.Contains(types, t => (string?)t["name"] == "DbgTest.Program");
		}

		// A path that does not exist is reported cleanly (a tool error), naming what was not found — not a
		// raw transport fault.
		[DbgFact]
		public void A_missing_module_is_reported_cleanly() {
			var error = Dbg.CallExpectingError("deobfuscate", new JObject {
				["module"] = "does-not-exist.dll",
			});

			Assert.False(string.IsNullOrWhiteSpace(error));
			Assert.Contains("does-not-exist.dll", error, StringComparison.OrdinalIgnoreCase);
		}

		// The lockdown boundary: dynamic string decryption would run the target's own code, so it is
		// refused with a message that says so — nothing is spawned or written.
		[DbgFact]
		public void Dynamic_string_decryption_is_rejected() {
			var error = Dbg.CallExpectingError("deobfuscate", new JObject {
				["module"] = Dbg.FixtureDll(),
				["string_decrypt"] = "dynamic",
			});

			Assert.Contains("dynamic", error, StringComparison.OrdinalIgnoreCase);
			// The reason must mention that it would execute the target's code (the whole point of the lock).
			Assert.Contains("code", error, StringComparison.OrdinalIgnoreCase);
		}

		static byte[] ReadHead(string path, int count) {
			var head = new byte[count];
			using var fs = File.OpenRead(path);
			var read = 0;
			while (read < count) {
				var n = fs.Read(head, read, count - read);
				if (n == 0)
					break;
				read += n;
			}
			Assert.Equal(count, read);
			return head;
		}
	}
}
