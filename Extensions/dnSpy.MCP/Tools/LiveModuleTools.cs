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
using dnlib.PE;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.DotNet.Metadata;
using dnSpy.MCP.Server;
using Newtonsoft.Json.Linq;
using static dnSpy.MCP.Tools.JsonUtils;

namespace dnSpy.MCP.Tools {
	/// <summary>
	/// Tools that reach into the *live* debuggee to analyse a module in the form the CLR actually loaded it —
	/// so a packed/protected assembly, whose on-disk file will not parse, can still be read once it is
	/// unpacked in memory. Both require a paused process (dbg_start then dbg_break, or break at a breakpoint).
	/// </summary>
	sealed class LiveModuleTools {
		readonly DbgAccess dbg;
		readonly Lazy<DbgMetadataService> metaService;

		public LiveModuleTools(DbgAccess dbg, Lazy<DbgMetadataService> metaService) {
			this.dbg = dbg;
			this.metaService = metaService;
		}

		public IEnumerable<ToolDef> Create() {
			yield return new ToolDef("dump_module",
				"Dump a module loaded in the debugged process to a file on disk — the in-memory image, so a packed/protected assembly is captured in its unpacked form. Then point the static tools (list_types, decompile, search, extract_iocs) at the dumped file. Requires a paused process; list modules with dbg_modules.",
				Schema.Object(
					("module", Schema.Str("Loaded module name or file name (from dbg_modules)"), true),
					("save_path", Schema.Str("Optional file path to write to (default: a temp file)"), false)),
				DumpModule);

			yield return new ToolDef("mem_load",
				"Load a module from the debugged process's memory (its unpacked in-memory form) into dnSpy, then analyse it by name with the static tools. Like dump_module but keeps it in dnSpy instead of writing a file. Requires a paused process.",
				Schema.Object(
					("module", Schema.Str("Loaded module name or file name (from dbg_modules)"), true)),
				MemLoad);
		}

		string DumpModule(JObject args) {
			var name = (string?)args["module"] ?? throw new ArgumentException("'module' is required");
			var savePath = (string?)args["save_path"];

			var (raw, layout, resolvedName) = dbg.Invoke(() => {
				var m = FindModule(name);
				if (m.IsDynamic)
					throw new InvalidOperationException("module is dynamic; it has no contiguous image to dump");
				if (!m.HasAddress || m.ImageLayout == DbgImageLayout.Unknown)
					throw new InvalidOperationException("module has no readable image in memory");
				var buf = new byte[m.Size];
				m.Process.ReadMemory(m.Address, buf, 0, (int)m.Size);
				return (buf, m.ImageLayout, m.Name);
			}, timeoutMs: 60000);

			// A memory-layout image spreads sections at their RVAs; rewrite to on-disk (file) layout so dnlib
			// and the static tools can parse it. If that fails (odd/packed headers), fall back to the raw dump.
			var data = raw;
			var converted = false;
			if (layout == DbgImageLayout.Memory) {
				try { data = MemoryToFile(raw); converted = true; }
				catch { data = raw; }
			}

			var path = string.IsNullOrEmpty(savePath)
				? System.IO.Path.Combine(System.IO.Path.GetTempPath(),
					"dnspymcp-dump-" + Guid.NewGuid().ToString("N").Substring(0, 8) + "-" + SafeName(resolvedName))
				: savePath!;
			System.IO.File.WriteAllBytes(path, data);
			return Json(new JObject {
				["module"] = resolvedName,
				["imageLayout"] = layout.ToString(),
				["convertedToFileLayout"] = converted,
				["bytes"] = data.Length,
				["savedTo"] = path,
				["hint"] = "point list_types / decompile / search / extract_iocs at savedTo",
			});
		}

		string MemLoad(JObject args) {
			var name = (string?)args["module"] ?? throw new ArgumentException("'module' is required");
			var loaded = dbg.Invoke(() => {
				var m = FindModule(name);
				if (m.Process.State != DbgProcessState.Paused)
					throw new InvalidOperationException("the process must be paused (dbg_break) to read module metadata from memory");
				var md = metaService.Value.TryGetMetadata(m, DbgLoadModuleOptions.ForceMemory)
					?? throw new InvalidOperationException("could not read the module's metadata from memory (is it a .NET module?)");
				return md.Name?.String ?? md.Assembly?.Name?.String ?? m.Name;
			}, timeoutMs: 60000);
			return Json(new JObject {
				["loaded"] = loaded,
				["hint"] = $"now call list_types / decompile / search with module = \"{loaded}\"",
			});
		}

		DbgModule FindModule(string name) {
			var mods = dbg.DbgManager.Processes.SelectMany(p => p.Runtimes).SelectMany(r => r.Modules).ToArray();
			return mods.FirstOrDefault(x => Matches(x, name))
				?? throw new InvalidOperationException($"no loaded module matching '{name}' (use dbg_modules; a process must be paused)");
		}

		static bool Matches(DbgModule m, string name) =>
			string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase) ||
			string.Equals(System.IO.Path.GetFileName(m.Filename ?? ""), name, StringComparison.OrdinalIgnoreCase) ||
			string.Equals(m.Filename, name, StringComparison.OrdinalIgnoreCase);

		static string SafeName(string? name) {
			name = string.IsNullOrEmpty(name) ? "module.bin" : name!;
			foreach (var c in System.IO.Path.GetInvalidFileNameChars())
				name = name.Replace(c, '_');
			return name;
		}

		// Rewrite a memory-layout PE image to file layout (copy each section from its RVA to its file offset).
		// Mirrors dnSpy's PEFilesSaver.WritePEFile.
		static byte[] MemoryToFile(byte[] raw) {
			using var pe = new PEImage(raw, ImageLayout.Memory, verify: true);
			var headers = (int)pe.ImageNTHeaders.OptionalHeader.SizeOfHeaders;
			var size = headers;
			foreach (var s in pe.ImageSectionHeaders)
				size = Math.Max(size, (int)s.PointerToRawData + (int)s.SizeOfRawData);
			var dst = new byte[size];
			Array.Copy(raw, 0, dst, 0, Math.Min(headers, raw.Length));
			foreach (var s in pe.ImageSectionHeaders) {
				int va = (int)s.VirtualAddress, off = (int)s.PointerToRawData, len = (int)s.SizeOfRawData;
				if (va < 0 || off < 0 || len < 0 || va + len > raw.Length || off + len > dst.Length)
					continue; // skip a section that would overrun (corrupt/packed header)
				Array.Copy(raw, va, dst, off, len);
			}
			return dst;
		}
	}
}
