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
using Xunit;

namespace dnSpy.MCP.IntegrationTests {
	/// <summary>
	/// Dumps a module out of the paused debuggee's memory to a loadable on-disk assembly, then reads that
	/// dump back with the static tools. The tool exists for a packed module unpacked in RAM; this suite
	/// proves the round trip on the (already-unpacked) fixture — the true RAM!=disk case is validated by a
	/// manual e2e against a real obfuscated module, not here.
	/// </summary>
	[Collection("dnSpy")]
	public class DumpModuleIntegrationTests : IDisposable {
		readonly List<string> tempDumps = new();

		public DumpModuleIntegrationTests() {
			Dbg.Reset();
			PauseInsideAdd();
		}

		public void Dispose() {
			Dbg.Reset();
			// The dumps go to %TEMP%; drop them so a run does not litter. dnSpy may still hold one open
			// (list_types/decompile opened it), so deletion is best-effort.
			foreach (var path in tempDumps) {
				try { File.Delete(path); }
				catch { /* best effort */ }
			}
		}

		/// <summary>Stops the fixture inside Program.Add (a == 7), where dbgtest.dll is loaded and mapped.</summary>
		static void PauseInsideAdd() {
			Dbg.Call("bp_add", new JObject {
				["module"] = Dbg.FixtureDll(),
				["token"] = Dbg.TokenOf("DbgTest.Program.Add"),
				["condition"] = "a == 7",
			});
			Dbg.Call("dbg_start", new JObject { ["path"] = Dbg.FixtureDll() });
			Dbg.WaitForBreak();
		}

		// The baseline that MUST pass: dump the fixture's own module out of memory and prove the result is a
		// real, parseable assembly whose metadata and method bodies survived the memory->file fold.
		[DbgFact]
		public void Dumping_the_paused_fixture_produces_a_loadable_assembly() {
			var res = Dbg.CallJson("dump_module", new JObject { ["module"] = "dbgtest.dll" });

			var savedTo = (string?)res["savedTo"]
				?? throw new InvalidOperationException("dump_module returned no savedTo");
			tempDumps.Add(savedTo);

			// The dump exists, is non-empty, and starts with the PE 'MZ' signature.
			Assert.True(File.Exists(savedTo), $"dump_module reported {savedTo} but the file is missing");
			Assert.True((long?)res["bytes"] > 0, "the dump is empty");
			Assert.True((long?)res["imageSize"] > 0, "the reported image size is zero");
			Assert.Equal(new byte[] { 0x4D, 0x5A }, ReadHead(savedTo, 2)); // 'M','Z'

			// A normally loaded module is mapped in memory layout, so the tool must have folded it back to
			// file layout for the round trip below to work at all.
			if ((string?)res["imageLayout"] == "Memory")
				Assert.True((bool?)res["convertedToFileLayout"], "a memory-layout image was not converted to file layout");

			// The dump's metadata is intact: list_types, pointed at the file, finds the fixture's type.
			var types = (JArray)Dbg.CallJson("list_types", new JObject {
				["module"] = savedTo,
				["filter"] = "DbgTest.Program",
			})["types"]!;
			Assert.Contains(types, t => (string?)t["name"] == "DbgTest.Program");

			// And its method bodies survived the fold: IL disassembly reads Add straight out of the dump,
			// needing no external assembly resolution, so this depends only on the dump's own correctness.
			var il = Dbg.Call("decompile", new JObject {
				["module"] = savedTo,
				["method"] = "DbgTest.Program.Add",
				["format"] = "il",
			});
			Assert.Contains("Add", il);
			Assert.Contains("ret", il, StringComparison.OrdinalIgnoreCase);
		}

		// No session at all: the tool must report it cleanly (a tool error), not throw a raw transport fault.
		[DbgFact]
		public void A_dump_with_no_paused_session_is_reported_cleanly() {
			Dbg.Reset(); // drop the session the constructor started — nothing is loaded or paused now

			var error = Dbg.CallExpectingError("dump_module", new JObject { ["module"] = "dbgtest.dll" });

			Assert.False(string.IsNullOrWhiteSpace(error));
			Assert.Contains("module", error, StringComparison.OrdinalIgnoreCase);
		}

		// A name that matches nothing loaded is reported cleanly, naming what was not found.
		[DbgFact]
		public void An_unknown_module_is_reported_cleanly() {
			var error = Dbg.CallExpectingError("dump_module", new JObject { ["module"] = "does-not-exist.dll" });

			Assert.Contains("does-not-exist.dll", error, StringComparison.OrdinalIgnoreCase);
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
