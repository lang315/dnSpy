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
using dnSpy.MCP.Server;
using Newtonsoft.Json.Linq;
using static dnSpy.MCP.Tools.JsonUtils;

namespace dnSpy.MCP.Tools {
	/// <summary>
	/// Reaches into the *live*, paused debuggee and copies a module's image straight out of process memory
	/// to a file on disk — so a packed/protected assembly, whose on-disk file will not parse, can still be
	/// read once the CLR has unpacked it in RAM. The static tools (list_types, decompile, search,
	/// extract_iocs) are then pointed at the dump.
	///
	/// Three repairs together make a packed module loadable:
	///  * Size: the true in-memory image size is derived from the section table parsed out of process memory
	///    — not from <see cref="DbgModule.Size"/> and not from OptionalHeader.SizeOfImage. On a large
	///    obfuscated module both of those are routinely smaller than the real image (packers shrink/corrupt
	///    them), so trusting them truncates the high sections. Walking the section table replicates dnSpy's
	///    own PortableExecutableHelper.GetImageSize.
	///  * Unmap: for a memory-layout module the full image is preserved verbatim and its section table is
	///    identity-mapped (file offset == RVA, the pe-sieve/Scylla "unmap") rather than compacted back to the
	///    on-disk section sizes. A packer's unpacked metadata lives in sections it only grew or decrypted in
	///    memory (VirtualSize > SizeOfRawData, or virtual-only with SizeOfRawData == 0); compacting would drop
	///    exactly those bytes, so instead every section stays at its RVA and dnlib's file-layout loader reads
	///    the preserved metadata straight out of the image. The dump is therefore memory-sized (larger than
	///    the original on-disk file), which is expected and correct for an unpacked dump.
	///  * COR20 repair: a common anti-dump trick zeroes the .NET data directory (DataDirectory[14]) so dnlib
	///    rejects the dump with "BadImageFormatException: .NET data directory RVA is 0". When it is zero, the
	///    CLI header and metadata root are found by scanning the identity-mapped image and the directory is
	///    rebuilt to point at them.
	/// </summary>
	sealed class LiveModuleTools {
		// The PE header fields dump_module needs to size the full in-memory image from the section table,
		// captured out of process memory as plain data so it need not re-touch a live PE off the dispatcher.
		readonly struct PeHeader {
			public readonly uint SizeOfHeaders;
			public readonly uint SectionAlignment;
			public readonly Section[] Sections;
			public PeHeader(uint sizeOfHeaders, uint sectionAlignment, Section[] sections) {
				SizeOfHeaders = sizeOfHeaders;
				SectionAlignment = sectionAlignment;
				Sections = sections;
			}
		}

		// One section header, all offsets widened to long so the size arithmetic below can never overflow or
		// wrap even if a packer wrote absurd RVA/size fields.
		readonly struct Section {
			public readonly long VirtualAddress;
			public readonly long VirtualSize;
			public readonly long SizeOfRawData;
			public Section(long virtualAddress, long virtualSize, long sizeOfRawData) {
				VirtualAddress = virtualAddress;
				VirtualSize = virtualSize;
				SizeOfRawData = sizeOfRawData;
			}
		}

		// The finished dump plus the facts reported back to the caller. Everything here is plain data, so it
		// leaves the debugger dispatcher safely and the (slow) file write can happen off that thread.
		readonly struct DumpResult {
			public readonly byte[] Data;
			public readonly long ImageSize;
			public readonly DbgImageLayout Layout;
			public readonly bool Unmapped;
			public readonly bool Cor20Reconstructed;
			public readonly string? Note;
			public readonly string ModuleName;
			public DumpResult(byte[] data, long imageSize, DbgImageLayout layout, bool unmapped,
					bool cor20Reconstructed, string? note, string moduleName) {
				Data = data;
				ImageSize = imageSize;
				Layout = layout;
				Unmapped = unmapped;
				Cor20Reconstructed = cor20Reconstructed;
				Note = note;
				ModuleName = moduleName;
			}
		}

		// A computed or declared image size past this is treated as corrupt: real modules, even heavily
		// obfuscated ones, are far smaller, and the cap keeps a bad header from forcing a huge allocation.
		const long MaxImageSize = 512L * 1024 * 1024;
		// Matches PortableExecutableHelper's probe: enough for the DOS + NT headers and the section table.
		const int HeaderProbeSize = 0x2000;
		// The fixed size of IMAGE_COR20_HEADER (the CLI header) — its `cb` field, and the size we write back.
		const uint Cor20HeaderSize = 72;

		readonly DbgAccess dbg;

		public LiveModuleTools(DbgAccess dbg) => this.dbg = dbg;

		public IEnumerable<ToolDef> Create() {
			yield return new ToolDef("dump_module",
				"Dump a module loaded in the debugged process to a file on disk, read straight out of process memory, so a packed/protected assembly is captured in its unpacked, in-RAM form. Then point the static tools (list_types, decompile, search, extract_iocs) at the dumped file. The true image size is taken from the section table read out of memory — not the often-corrupt PE SizeOfImage and not the debugger's reported module size — so the high sections of a large obfuscated module are not truncated. For a memory-layout module the full in-memory image is preserved and its section table is identity-mapped (file offset == RVA, the pe-sieve/Scylla \"unmap\"), so metadata a packer only grew or decrypted in memory (VirtualSize > SizeOfRawData, or virtual-only sections) survives; the dump is therefore memory-sized (larger than the on-disk file), which is expected. If a protector has zeroed the .NET data directory (a common anti-dump), it is reconstructed by locating the CLI header and metadata root so the dump still loads. Requires a paused process (hit a breakpoint or dbg_break); list candidates with dbg_modules. Known limits: method bodies a protector decrypts lazily at call time are present only if already materialised in memory; and a module whose PE header is corrupt or anti-tampered falls back to a raw memory copy sized by the debugger (see the 'note' field), which may need manual repair.",
				Schema.Object(
					("module", Schema.Str("Loaded module name or file name, as shown by dbg_modules"), true),
					("save_path", Schema.Str("Optional file path to write to (default: a temp file)"), false)),
				DumpModule);
		}

		string DumpModule(JObject args) {
			var name = (string?)args["module"] ?? throw new ArgumentException("'module' is required");
			var savePath = (string?)args["save_path"];

			// Everything that touches a debugger object (finding the module, reading its process memory,
			// reading its layout/size) runs on the DbgManager dispatcher. The in-place unmap and COR20 repair
			// are pure byte work but are kept here too so nothing reads the DbgModule off-thread; only the
			// finished byte[] and primitives cross back out. The file write is deliberately left outside the
			// dispatcher below so a large (memory-sized) dump does not block the debugger on disk I/O.
			var result = dbg.Invoke(() => {
				var m = FindModule(name);
				if (m.IsDynamic)
					throw new InvalidOperationException("module is dynamic (emitted at runtime); it has no contiguous PE image to dump");
				if (!m.HasAddress)
					throw new InvalidOperationException("module has no base address/size in memory and cannot be dumped");
				if (m.Process.State != DbgProcessState.Paused)
					throw new InvalidOperationException("the process must be paused (hit a breakpoint or call dbg_break) before its memory can be read");

				var process = m.Process;
				var baseAddr = m.Address;

				// (1) Probe the PE header out of process memory and verify the MZ signature.
				var hdr = new byte[HeaderProbeSize];
				process.ReadMemory(baseAddr, hdr, 0, hdr.Length);
				if (BitConverter.ToUInt16(hdr, 0) != 0x5A4D)
					throw new InvalidOperationException($"no MZ signature at 0x{baseAddr:X}; the module base does not point at a PE image in memory");

				// (2) Derive the true image size from the parsed section table. This is the fix: NOT
				// DbgModule.Size (m.Size) and NOT OptionalHeader.SizeOfImage, both of which a packer routinely
				// shrinks or corrupts — reading only that many bytes truncated earlier dumps. Fall back to the
				// debugger-reported size only when the header itself will not parse or gives an absurd size.
				string? note = null;
				PeHeader? parsed = TryParseHeader(hdr);
				long imageSize;
				if (parsed is PeHeader ph && TryGetImageSize(ph, out var sectionSize)) {
					imageSize = sectionSize;
				}
				else {
					imageSize = m.Size;
					note = parsed is null
						? "PE header did not parse (corrupt or anti-tampered); dumped the raw memory image at the debugger-reported size, so it may be truncated or need manual repair"
						: "section table gave an implausible image size; fell back to the debugger-reported module size, which can truncate a packed image";
				}
				if (imageSize <= 0 || imageSize > MaxImageSize) {
					var clamped = Math.Min(Math.Max(imageSize, HeaderProbeSize), MaxImageSize);
					note = (note is null ? "" : note + "; ") + $"image size {imageSize} was out of range and was clamped to {clamped}";
					imageSize = clamped;
				}

				// (3) Read the whole image. An unmapped tail page reads back as zeros (ReadMemory never throws),
				// which is exactly right for a section that is reserved in the image but not committed.
				var raw = new byte[imageSize];
				process.ReadMemory(baseAddr, raw, 0, raw.Length);

				// (4) A memory-layout image has every section spread at its RVA. Do NOT compact it back to the
				// on-disk section sizes — a packer's unpacked metadata lives in sections it grew or decrypted
				// only in memory, and compacting drops exactly those bytes. Instead keep the full image verbatim
				// and rewrite its section table in place so every file offset identity-maps to its RVA; dnlib's
				// file-layout loader then reads the preserved metadata straight out of it. A file-layout image
				// is already on-disk form and is left untouched.
				var unmapped = false;
				if (m.ImageLayout == DbgImageLayout.Memory && parsed is not null) {
					UnmapSectionTable(raw);
					unmapped = true;
				}

				// (5) Repair a zeroed COR20 / .NET data directory (a common anti-dump). Runs after the unmap so
				// it can rely on the identity map (file offset == RVA); it no-ops when the directory is already
				// present, which is the normal case (including the fixture and any file-layout image).
				var cor20Reconstructed = ReconstructCor20Directory(raw);
				if (cor20Reconstructed)
					note = (note is null ? "" : note + "; ") +
						"the .NET data directory (COR20) was zeroed (anti-dump) and has been reconstructed";

				return new DumpResult(raw, imageSize, m.ImageLayout, unmapped, cor20Reconstructed, note, m.Name);
			}, timeoutMs: 60000);

			// (6) Write the dump. Off the dispatcher: a memory-sized image should not stall the debugger on I/O.
			var path = string.IsNullOrEmpty(savePath)
				? System.IO.Path.Combine(System.IO.Path.GetTempPath(),
					"dnspymcp-dump-" + Guid.NewGuid().ToString("N").Substring(0, 8) + "-" + SafeName(result.ModuleName))
				: savePath!;
			System.IO.File.WriteAllBytes(path, result.Data);

			var json = new JObject {
				["module"] = result.ModuleName,
				["savedTo"] = path,
				["bytes"] = result.Data.Length,
				["imageSize"] = result.ImageSize,
				["imageLayout"] = result.Layout.ToString(),
				// convertedToFileLayout means "now loadable as a file"; for a memory image that is the unmap.
				["convertedToFileLayout"] = result.Unmapped,
				["unmapped"] = result.Unmapped,
				["cor20Reconstructed"] = result.Cor20Reconstructed,
			};
			if (result.Note is not null)
				json["note"] = result.Note;
			return Json(json);
		}

		DbgModule FindModule(string name) {
			var mods = dbg.DbgManager.Processes.SelectMany(p => p.Runtimes).SelectMany(r => r.Modules);
			return mods.FirstOrDefault(x => Matches(x, name))
				?? throw new InvalidOperationException($"no loaded module matching '{name}' (list them with dbg_modules; a process must be paused)");
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

		// Parse just the probed header region as a memory-layout image and capture the header/section fields.
		// The DOS + NT headers and the section table are identical in file and memory layout, so parsing the
		// first HeaderProbeSize bytes as Memory is enough regardless of the module's real layout — this is the
		// same trick PortableExecutableHelper uses. Returns null if the header will not parse (anti-tamper).
		static PeHeader? TryParseHeader(byte[] hdr) {
			try {
				using var pe = new PEImage(hdr, null, ImageLayout.Memory, verify: true);
				var opt = pe.ImageNTHeaders.OptionalHeader;
				var sections = new Section[pe.ImageSectionHeaders.Count];
				for (int i = 0; i < sections.Length; i++) {
					var s = pe.ImageSectionHeaders[i];
					sections[i] = new Section((long)(ulong)s.VirtualAddress, s.VirtualSize, s.SizeOfRawData);
				}
				return new PeHeader(opt.SizeOfHeaders, opt.SectionAlignment, sections);
			}
			catch {
				return null;
			}
		}

		// The true in-memory image size, from the section table — the memory-layout arm of dnSpy's own
		// PortableExecutableHelper.GetImageSize: max over sections of AlignUp(VirtualAddress +
		// max(VirtualSize, SizeOfRawData), SectionAlignment), floored at the aligned header size. Returns false
		// for an implausible result (zero, below the headers, or above the sanity cap) so the caller falls back.
		static bool TryGetImageSize(PeHeader ph, out long imageSize) {
			imageSize = 0;
			var len = AlignUp(ph.SizeOfHeaders, ph.SectionAlignment);
			foreach (var s in ph.Sections) {
				var end = AlignUp(s.VirtualAddress + Math.Max(s.VirtualSize, s.SizeOfRawData), ph.SectionAlignment);
				if (end > len)
					len = end;
			}
			if (len <= 0 || len < ph.SizeOfHeaders || len > MaxImageSize)
				return false;
			imageSize = len;
			return true;
		}

		static long AlignUp(long value, uint alignment) =>
			alignment == 0 ? value : (value + alignment - 1) & ~((long)alignment - 1);

		// Rewrite the section table of a preserved memory image in place so every file offset identity-maps to
		// its RVA: for each section PointerToRawData := VirtualAddress and SizeOfRawData := AlignUp(max(
		// VirtualSize, SizeOfRawData), SectionAlignment), and OptionalHeader.FileAlignment := SectionAlignment
		// (so those PointerToRawData values are FileAlignment-aligned). dnlib's file-layout loader then reads
		// section data straight from the memory image, so a section a packer only grew/decrypted in memory
		// keeps its bytes — the pe-sieve/Scylla "unmap" a compacting rebuild cannot do. Reads the layout from
		// dst's own header bytes (valid for PE32 and PE32+, whose SectionAlignment/FileAlignment share these
		// offsets); every read and write is bounds-guarded so a truncated/odd buffer is left as a plain memory
		// dump rather than throwing. Because imageSize was derived from these same sections, VirtualAddress +
		// the new SizeOfRawData never exceeds dst.Length for an aligned image, so the identity map is valid.
		static void UnmapSectionTable(byte[] dst) {
			if (dst.Length < 0x40)
				return;
			long peBase = ReadU32(dst, 0x3C);
			long fileHdr = peBase + 4;   // IMAGE_FILE_HEADER
			long optBase = peBase + 24;  // IMAGE_OPTIONAL_HEADER (fileHdr + 20)
			// Need the whole file header and the SectionAlignment/FileAlignment fields (optBase+32, optBase+36).
			if (peBase < 0 || fileHdr + 20 > dst.Length || optBase + 40 > dst.Length)
				return;
			if (ReadU16(dst, peBase) != 0x4550) // "PE" — cheap sanity before writing anything
				return;

			int numberOfSections = ReadU16(dst, fileHdr + 2);
			int sizeOfOptionalHeader = ReadU16(dst, fileHdr + 16);
			uint sectionAlignment = ReadU32(dst, optBase + 32);
			if (sectionAlignment == 0)
				return; // corrupt alignment: leave the image as a plain memory dump

			long secBase = optBase + sizeOfOptionalHeader;

			// FileAlignment = SectionAlignment.
			WriteU32(dst, optBase + 36, sectionAlignment);

			for (int i = 0; i < numberOfSections; i++) {
				long h = secBase + (long)i * 40; // IMAGE_SECTION_HEADER is 40 bytes
				if (h < 0 || h + 24 > dst.Length)
					break; // section table runs past the buffer — stop rather than overrun
				uint virtualSize = ReadU32(dst, h + 8);
				uint virtualAddress = ReadU32(dst, h + 12);
				uint sizeOfRawData = ReadU32(dst, h + 16);
				uint newRawSize = AlignUpU32(Math.Max(virtualSize, sizeOfRawData), sectionAlignment);
				WriteU32(dst, h + 16, newRawSize);     // SizeOfRawData := aligned virtual size
				WriteU32(dst, h + 20, virtualAddress); // PointerToRawData := VirtualAddress
			}
		}

		// Repair a zeroed COR20 / .NET data directory (DataDirectory[14], the "COM descriptor"). A common
		// anti-dump trick zeroes it so a dumped module fails dnlib's load with "BadImageFormatException: .NET
		// data directory RVA is 0". Because the image is identity-mapped (file offset == RVA) at this point,
		// every RVA is a direct offset into dst, so the CLI header and metadata root are found by scanning and
		// the directory rebuilt to point at them. No-ops (returns false) when the directory is already present
		// — the normal case, including the fixture and any file-layout image. Fully bounds-guarded: on any miss
		// it leaves the dump unrepaired and returns false rather than throwing.
		// internal (not private) so the Tier 1 test project can drive it directly against a synthetic image.
		internal static bool ReconstructCor20Directory(byte[] dst) {
			if (dst.Length < 0x40)
				return false;
			long peBase = ReadU32(dst, 0x3C);
			long optBase = peBase + 24;
			if (peBase < 0 || optBase + 2 > dst.Length)
				return false;
			ushort magic = ReadU16(dst, optBase);
			long ddBase = optBase + (magic == 0x20B ? 112 : 96); // DataDirectory: PE32+ @ +112, PE32 @ +96
			long cor20 = ddBase + 14 * 8;                        // DataDirectory[14]: RVA @ cor20, Size @ cor20+4
			if (cor20 < 0 || cor20 + 8 > dst.Length)
				return false;
			if (ReadU32(dst, cor20) != 0)
				return false; // already present — nothing to repair

			// (a) The metadata root, identified by its "BSJB" signature; its offset == RVA under the identity map.
			long mdRva = IndexOf(dst, 0x42, 0x53, 0x4A, 0x42);
			if (mdRva < 0)
				return false;

			// (b) The CLI header (IMAGE_COR20_HEADER, 72 bytes): its cb field is 72 and its MetaData directory
			// RVA (@ +8) points at the metadata root. Restrict to a candidate inside a section, not the root.
			long cliRva = FindCliHeader(dst, mdRva);
			if (cliRva < 0)
				return false;

			// (c) DataDirectory[14] = { VirtualAddress = cliRva, Size = 72 }.
			WriteU32(dst, cor20, (uint)cliRva);
			WriteU32(dst, cor20 + 4, Cor20HeaderSize);
			return true;
		}

		// The RVA of the CLI header: the first offset (inside a section, not the metadata root) whose cb field
		// is 72 and whose MetaData.VirtualAddress equals mdRva. Identity-mapped, so offset == RVA.
		static long FindCliHeader(byte[] dst, long mdRva) {
			var ranges = SectionRanges(dst);
			if (ranges.Count == 0)
				return -1;
			var wantMdRva = (uint)mdRva;
			for (long c = 0; c + 16 <= dst.Length; c++) {
				// cb == 72 (0x48), little-endian, as a fast prefilter before the rarer checks.
				if (dst[c] != 0x48 || dst[c + 1] != 0 || dst[c + 2] != 0 || dst[c + 3] != 0)
					continue;
				if (c == mdRva)
					continue;
				if (ReadU32(dst, c + 8) != wantMdRva)
					continue;
				if (InAnyRange(ranges, c))
					return c;
			}
			return -1;
		}

		// The [VirtualAddress, VirtualAddress + VirtualSize) span of each non-empty section, read from dst's
		// header. VirtualSize/VirtualAddress are untouched by the unmap, so these are the real virtual ranges.
		static List<(long start, long end)> SectionRanges(byte[] dst) {
			var ranges = new List<(long start, long end)>();
			if (dst.Length < 0x40)
				return ranges;
			long peBase = ReadU32(dst, 0x3C);
			long fileHdr = peBase + 4;
			long optBase = peBase + 24;
			if (peBase < 0 || fileHdr + 20 > dst.Length)
				return ranges;
			int numberOfSections = ReadU16(dst, fileHdr + 2);
			int sizeOfOptionalHeader = ReadU16(dst, fileHdr + 16);
			long secBase = optBase + sizeOfOptionalHeader;
			for (int i = 0; i < numberOfSections; i++) {
				long h = secBase + (long)i * 40;
				if (h < 0 || h + 16 > dst.Length)
					break;
				long virtualSize = ReadU32(dst, h + 8);
				long virtualAddress = ReadU32(dst, h + 12);
				if (virtualSize == 0)
					continue;
				ranges.Add((virtualAddress, virtualAddress + virtualSize));
			}
			return ranges;
		}

		static bool InAnyRange(List<(long start, long end)> ranges, long value) {
			foreach (var (start, end) in ranges)
				if (value >= start && value < end)
					return true;
			return false;
		}

		// The first index where the four bytes b0..b3 appear consecutively in b, or -1.
		static long IndexOf(byte[] b, byte b0, byte b1, byte b2, byte b3) {
			for (long i = 0; i + 4 <= b.Length; i++) {
				if (b[i] == b0 && b[i + 1] == b1 && b[i + 2] == b2 && b[i + 3] == b3)
					return i;
			}
			return -1;
		}

		static uint AlignUpU32(uint value, uint alignment) {
			if (alignment == 0)
				return value;
			ulong aligned = ((ulong)value + alignment - 1) & ~((ulong)alignment - 1);
			return aligned > uint.MaxValue ? value : (uint)aligned;
		}

		static ushort ReadU16(byte[] b, long offset) => BitConverter.ToUInt16(b, (int)offset);
		static uint ReadU32(byte[] b, long offset) => BitConverter.ToUInt32(b, (int)offset);

		static void WriteU32(byte[] b, long offset, uint value) {
			b[offset] = (byte)value;
			b[offset + 1] = (byte)(value >> 8);
			b[offset + 2] = (byte)(value >> 16);
			b[offset + 3] = (byte)(value >> 24);
		}
	}
}
