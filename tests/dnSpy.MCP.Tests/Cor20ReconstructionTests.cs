using System;
using dnSpy.MCP.Tools;
using Xunit;

namespace dnSpy.MCP.Tests {
	/// <summary>
	/// LiveModuleTools.ReconstructCor20Directory repairs a common anti-dump trick: a protector zeroes the
	/// PE .NET data directory (DataDirectory[14], the COR20 / "COM descriptor"), so a dumped module fails
	/// dnlib's load with "BadImageFormatException: .NET data directory RVA is 0". On an identity-mapped
	/// image (file offset == RVA) the repair finds the "BSJB" metadata root and the CLI header
	/// (IMAGE_COR20_HEADER, cb == 72, whose MetaData RVA points at that root) and rewrites DataDirectory[14]
	/// to { cliRva, 72 }.
	///
	/// These build a MINIMAL synthetic unmapped PE32 image in a byte[] and check the reconstruction. It
	/// mirrors the anti-dump case validated end-to-end on a real NETGuard-protected module — COR20 directory
	/// zeroed, but the "BSJB" metadata root and the CLI header both still present in the in-memory dump — so
	/// the directory could be rebuilt by scanning for them.
	/// </summary>
	public class Cor20ReconstructionTests {
		// Offsets into the synthetic image built by BuildValidImage. SizeOfImage is 0x6000.
		const int ELfanew = 0x3C;                         // DOS header -> e_lfanew (points at the PE header)
		const int PeBase = 0x80;                          // 'P','E',0,0
		const int OptBase = PeBase + 24;                  // 0x98  IMAGE_OPTIONAL_HEADER32 (PE sig 4 + file hdr 20)
		const int SecBase = OptBase + 0xE0;               // 0x178 section table (after SizeOfOptionalHeader = 224)
		const int Cor20RvaOffset = OptBase + 96 + 14 * 8; // 0x168 DataDirectory[14].VirtualAddress
		const int Cor20SizeOffset = Cor20RvaOffset + 4;   // 0x16C DataDirectory[14].Size
		const int CliHeaderRva = 0x2008;                  // IMAGE_COR20_HEADER, placed inside .text (RVA == offset)
		const int MetadataRootRva = 0x3000;               // "BSJB" metadata root, placed inside .text

		// The anti-dump case: DataDirectory[14] is zeroed but the CLI header and metadata root survive, so
		// the directory is rebuilt to point at the CLI header (RVA 0x2008) with the fixed 72-byte size.
		[Fact]
		public void Rebuilds_a_zeroed_cor20_directory_to_point_at_the_cli_header() {
			var image = BuildValidImage();

			var repaired = LiveModuleTools.ReconstructCor20Directory(image);

			Assert.True(repaired);
			Assert.Equal((uint)CliHeaderRva, BitConverter.ToUInt32(image, Cor20RvaOffset));
			Assert.Equal(72u, BitConverter.ToUInt32(image, Cor20SizeOffset));
		}

		// A dump whose .NET data directory is intact: the repair must be a pure no-op and touch nothing.
		[Fact]
		public void Leaves_an_already_present_cor20_directory_untouched() {
			var image = BuildValidImage();

			// Local writer so the two seed offsets read clearly (LE UInt32).
			void W32(int off, int v) {
				image[off] = (byte)v;
				image[off + 1] = (byte)(v >> 8);
				image[off + 2] = (byte)(v >> 16);
				image[off + 3] = (byte)(v >> 24);
			}
			W32(Cor20RvaOffset, 0x1234);
			W32(Cor20SizeOffset, 72);

			var repaired = LiveModuleTools.ReconstructCor20Directory(image);

			Assert.False(repaired);
			Assert.Equal(0x1234u, BitConverter.ToUInt32(image, Cor20RvaOffset));
			Assert.Equal(72u, BitConverter.ToUInt32(image, Cor20SizeOffset));
		}

		// No metadata root to rebuild toward: return false, leave DataDirectory[14] zeroed, never throw.
		[Fact]
		public void Returns_false_and_leaves_the_directory_zeroed_when_there_is_no_metadata_root() {
			var image = BuildValidImage();
			image[MetadataRootRva] = 0; // erase the 'B' of "BSJB" so the signature scan misses

			var repaired = LiveModuleTools.ReconstructCor20Directory(image);

			Assert.False(repaired);
			Assert.Equal(0u, BitConverter.ToUInt32(image, Cor20RvaOffset));
			Assert.Equal(0u, BitConverter.ToUInt32(image, Cor20SizeOffset));
		}

		// "BSJB" is present but no CLI header points at it (cb wiped): return false, leave it zeroed, never throw.
		[Fact]
		public void Returns_false_and_leaves_the_directory_zeroed_when_no_cli_header_matches() {
			var image = BuildValidImage();
			image[CliHeaderRva] = 0; // wipe the CLI header's cb (0x48) so no IMAGE_COR20_HEADER can be matched

			var repaired = LiveModuleTools.ReconstructCor20Directory(image);

			Assert.False(repaired);
			Assert.Equal(0u, BitConverter.ToUInt32(image, Cor20RvaOffset));
			Assert.Equal(0u, BitConverter.ToUInt32(image, Cor20SizeOffset));
		}

		// A minimal, unmapped (file offset == RVA) PE32 image whose COR20 / .NET data directory is zeroed but
		// whose CLI header and "BSJB" metadata root are present — the exact anti-dump shape the repair targets.
		// Only the fields ReconstructCor20Directory actually reads are populated; everything else stays zero.
		static byte[] BuildValidImage() {
			var image = new byte[0x6000];

			// Local writers keep the field offsets below readable (all values are small, positive LE integers).
			void W16(int off, int v) {
				image[off] = (byte)v;
				image[off + 1] = (byte)(v >> 8);
			}
			void W32(int off, int v) {
				image[off] = (byte)v;
				image[off + 1] = (byte)(v >> 8);
				image[off + 2] = (byte)(v >> 16);
				image[off + 3] = (byte)(v >> 24);
			}
			void WAscii(int off, string s) {
				for (int i = 0; i < s.Length; i++)
					image[off + i] = (byte)s[i];
			}

			// DOS header: 'MZ' + e_lfanew -> the PE header.
			WAscii(0, "MZ");
			W32(ELfanew, PeBase);

			// PE signature ('P','E',0,0) + IMAGE_FILE_HEADER.
			WAscii(PeBase, "PE");
			int fileHdr = PeBase + 4;
			W16(fileHdr + 0, 0x14C);   // Machine = i386
			W16(fileHdr + 2, 1);       // NumberOfSections
			W16(fileHdr + 16, 0xE0);   // SizeOfOptionalHeader = 224 (96 + 16 data dirs * 8)
			W16(fileHdr + 18, 0x2102); // Characteristics

			// IMAGE_OPTIONAL_HEADER32.
			W16(OptBase + 0, 0x10B);   // Magic = PE32 (not 0x20B, so DataDirectory begins at OptBase+96)
			W32(OptBase + 32, 0x2000); // SectionAlignment
			W32(OptBase + 36, 0x2000); // FileAlignment
			W32(OptBase + 56, 0x6000); // SizeOfImage
			W32(OptBase + 60, 0x200);  // SizeOfHeaders
			W32(OptBase + 92, 16);     // NumberOfRvaAndSizes
			// DataDirectory begins at OptBase+96 (0xF8); entry 14 (COR20) at 0x168 is left ZEROED.

			// Section table: one ".text", identity-mapped, covering RVAs [0x2000, 0x6000).
			WAscii(SecBase + 0, ".text");
			W32(SecBase + 8, 0x4000);  // VirtualSize
			W32(SecBase + 12, 0x2000); // VirtualAddress
			W32(SecBase + 16, 0x4000); // SizeOfRawData
			W32(SecBase + 20, 0x2000); // PointerToRawData == VirtualAddress (unmapped / identity map)

			// IMAGE_COR20_HEADER (CLI header) at RVA 0x2008: cb == 72, MetaData RVA -> the "BSJB" root.
			W32(CliHeaderRva + 0, 0x48);            // cb = 72 (the finder's prefilter)
			W16(CliHeaderRva + 4, 2);               // MajorRuntimeVersion (arbitrary)
			W16(CliHeaderRva + 6, 5);               // MinorRuntimeVersion (arbitrary)
			W32(CliHeaderRva + 8, MetadataRootRva); // MetaData.VirtualAddress -> 0x3000
			W32(CliHeaderRva + 12, 0x200);          // MetaData.Size (arbitrary)

			// Metadata root signature at RVA 0x3000.
			WAscii(MetadataRootRva, "BSJB");

			return image;
		}
	}
}
