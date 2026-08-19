using System;
using dnSpy.MCP.Tools;
using Xunit;

namespace dnSpy.MCP.Tests {
	/// <summary>
	/// Argument parsing for the memory and breakpoint tools. These take agent-authored strings, so
	/// they must accept the spellings an agent actually produces and reject the rest loudly — a
	/// silently mis-parsed address means writing to the wrong place in a live process.
	/// </summary>
	public class JsonUtilsTests {
		[Theory]
		[InlineData("DEAD")]
		[InlineData("0xDEAD")]
		[InlineData("0XDEAD")]
		[InlineData("DE AD")]
		[InlineData("DE-AD")]
		[InlineData("de,ad")]
		[InlineData("0xDE 0xAD")]
		[InlineData(" DE AD ")]
		public void Hex_bytes_accept_every_spelling_an_agent_might_use(string text) {
			Assert.Equal(new byte[] { 0xDE, 0xAD }, JsonUtils.ParseHexBytes(text, "bytes"));
		}

		[Fact]
		public void Hex_bytes_handle_a_nop_sled_written_per_byte() {
			Assert.Equal(new byte[] { 0x90, 0x90, 0x90 }, JsonUtils.ParseHexBytes("0x90 0x90 0x90", "bytes"));
		}

		[Theory]
		[InlineData("ABC")]      // odd number of digits
		[InlineData("ZZ")]       // not hex
		[InlineData("DEADZZ")]   // valid prefix, invalid tail
		public void Hex_bytes_reject_malformed_input(string text) {
			Assert.Throws<ArgumentException>(() => JsonUtils.ParseHexBytes(text, "bytes"));
		}

		[Fact]
		public void Hex_bytes_require_a_value() {
			var ex = Assert.Throws<ArgumentException>(() => JsonUtils.ParseHexBytes(null, "bytes"));
			Assert.Contains("bytes", ex.Message);
		}

		[Theory]
		[InlineData("0x10", 16UL)]
		[InlineData("0X10", 16UL)]
		[InlineData("16", 16UL)]
		[InlineData("0", 0UL)]
		[InlineData(" 0x7FFFFFFFFFFFFFFF ", 9223372036854775807UL)]
		public void Numbers_parse_as_hex_when_prefixed_and_decimal_otherwise(string text, ulong expected) {
			Assert.Equal(expected, JsonUtils.ParseULong(text, "address"));
		}

		[Theory]
		[InlineData("nope")]
		[InlineData("0x")]
		[InlineData("-1")]
		public void Numbers_reject_malformed_input(string text) {
			Assert.Throws<ArgumentException>(() => JsonUtils.ParseULong(text, "address"));
		}

		// A metadata token is 32-bit; a value that does not fit must be an error rather than a
		// silently truncated token pointing at some unrelated method.
		[Fact]
		public void A_token_wider_than_32_bits_is_out_of_range() {
			var ex = Assert.Throws<ArgumentException>(() => JsonUtils.ParseUInt("0x100000000", "token"));
			Assert.Contains("token", ex.Message);
		}

		[Fact]
		public void A_token_at_the_32_bit_boundary_is_accepted() {
			Assert.Equal(uint.MaxValue, JsonUtils.ParseUInt("0xFFFFFFFF", "token"));
		}

		[Theory]
		[InlineData(5, 1, 10, 5)]
		[InlineData(0, 1, 10, 1)]
		[InlineData(99, 1, 10, 10)]
		public void Clamp_bounds_a_value(int value, int min, int max, int expected) {
			Assert.Equal(expected, JsonUtils.Clamp(value, min, max));
		}
	}
}
