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
using System.Linq;
using dnSpy.Contracts.Debugger;
using dnSpy.MCP.Server;
using Newtonsoft.Json.Linq;
using static dnSpy.MCP.Tools.JsonUtils;

namespace dnSpy.MCP.Tools {
	/// <summary>Read and write debuggee process memory.</summary>
	sealed class MemoryTools {
		const int MaxReadBytes = 64 * 1024;

		readonly DbgAccess dbg;

		public MemoryTools(DbgAccess dbg) => this.dbg = dbg;

		DbgManager Mgr => dbg.DbgManager;

		public IEnumerable<ToolDef> Create() {
			yield return new ToolDef("dbg_read_memory",
				"Read raw bytes from the debuggee's memory and return them as hex. Unreadable addresses read back as zeros (the underlying API does not report read failures).",
				Schema.Object(
					("address", Schema.Str("Start address, hex (0x...) or decimal"), true),
					("size", Schema.Int("Number of bytes to read (max 65536)"), true),
					("pid", Schema.Int("Process id; defaults to the current process"), false)),
				ReadMemory);

			yield return new ToolDef("dbg_write_memory",
				"Write raw bytes (given as hex) into the debuggee's memory.",
				Schema.Object(
					("address", Schema.Str("Start address, hex or decimal"), true),
					("bytes", Schema.Str("Hex bytes to write, e.g. '90 90' or '0x9090'"), true),
					("pid", Schema.Int("Process id; defaults to the current process"), false)),
				WriteMemory);
		}

		string ReadMemory(JObject args) {
			var address = ParseULong((string?)args["address"], "address");
			var size = (int?)args["size"] ?? throw new ArgumentException("'size' is required");
			if (size <= 0 || size > MaxReadBytes)
				throw new ArgumentException($"'size' must be between 1 and {MaxReadBytes}");
			var pid = (int?)args["pid"];

			return dbg.Invoke(() => {
				var process = ResolveProcess(pid);
				var buffer = new byte[size];
				process.ReadMemory(address, buffer, 0, size);
				return Json(new JObject {
					["address"] = "0x" + address.ToString("X"),
					["size"] = size,
					["hex"] = BitConverter.ToString(buffer).Replace("-", ""),
				});
			});
		}

		string WriteMemory(JObject args) {
			var address = ParseULong((string?)args["address"], "address");
			var bytes = ParseHexBytes((string?)args["bytes"], "bytes");
			var pid = (int?)args["pid"];
			if (bytes.Length == 0)
				throw new ArgumentException("'bytes' is empty");

			return dbg.Invoke(() => {
				var process = ResolveProcess(pid);
				process.WriteMemory(address, bytes, 0, bytes.Length);
				// WriteMemory reports no failure signal, so read the bytes back and verify.
				var check = new byte[bytes.Length];
				process.ReadMemory(address, check, 0, check.Length);
				for (int i = 0; i < bytes.Length; i++) {
					if (check[i] != bytes[i])
						throw new InvalidOperationException($"write not confirmed at 0x{address + (ulong)i:X} (address may be unwritable)");
				}
				return $"wrote and verified {bytes.Length} byte(s) at 0x{address:X}";
			});
		}

		DbgProcess ResolveProcess(int? pid) {
			var processes = Mgr.Processes;
			if (processes.Length == 0)
				throw new InvalidOperationException("no debugged process");
			if (pid is null)
				return Mgr.CurrentProcess.Current ?? processes[0];
			return processes.FirstOrDefault(p => p.Id == pid.Value)
				?? throw new InvalidOperationException($"no process with pid {pid.Value}");
		}
	}
}
