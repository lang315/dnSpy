using System;
using System.Globalization;
using Newtonsoft.Json.Linq;
using Xunit;

namespace dnSpy.MCP.IntegrationTests {
	/// <summary>
	/// dbg_write_memory against a real debuggee. It is the one destructive tool that had never been
	/// run: it writes, then reads its own bytes back because the underlying WriteMemory reports no
	/// failure, and neither half of that had ever been shown to work.
	/// </summary>
	[Collection("dnSpy")]
	public class MemoryWriteIntegrationTests : IDisposable {
		public MemoryWriteIntegrationTests() {
			Dbg.Reset();
			PauseInsideAdd();
		}

		public void Dispose() => Dbg.Reset();

		/// <summary>
		/// Stops the fixture at Program.Add. Writing does not need a paused process, but reading the
		/// scratch buffer's address does: that comes from an expression evaluated in a stack frame.
		/// </summary>
		static void PauseInsideAdd() {
			Dbg.Call("bp_add", new JObject {
				["module"] = Dbg.FixtureDll(),
				["token"] = Dbg.TokenOf("DbgTest.Program.Add"),
			});
			Dbg.Call("dbg_start", new JObject { ["path"] = Dbg.FixtureDll() });
			Dbg.WaitForBreak();
		}

		/// <summary>
		/// An address that can never be written: Windows permanently reserves the lowest 64 KB of
		/// every address space, so nothing maps it and no write can land there.
		/// </summary>
		const string UnwritableAddress = "0x1000";

		/// <summary>
		/// The address of the first byte of DbgTest.Program.Scratch.
		///
		/// A managed array has no address a test could hard-code — the GC is free to move it, and the
		/// expression evaluator has no "address of" operator. The fixture therefore pins the array
		/// itself at startup and publishes the address in a static field, which this only has to read.
		///
		/// Asking the debugger to do the pinning instead does not work: func-evaluating
		/// GCHandle.Alloc(..., Pinned) or Marshal.AllocHGlobal(16) both answer
		/// {ArgumentOutOfRangeException}, while ordinary calls such as Math.Max(3, 4) and even
		/// DbgTest.Program.Add(2, 3) evaluate correctly — so it is those interop APIs specifically that
		/// the evaluation environment cannot run, not func-eval in general.
		/// </summary>
		static ulong ScratchAddress() {
			var res = Dbg.CallJson("dbg_eval", new JObject {
				["expression"] = "DbgTest.Program.ScratchAddress",
			});
			var address = Dbg.Number(res["value"]);
			Assert.True(address > 0, "the fixture published no address for its Scratch array");
			return (ulong)address;
		}

		static string Hex(ulong address) => "0x" + address.ToString("X");

		static string Write(string address, string bytes) =>
			Dbg.Call("dbg_write_memory", new JObject { ["address"] = address, ["bytes"] = bytes });

		static string ReadHex(string address, int size) =>
			(string)Dbg.CallJson("dbg_read_memory", new JObject { ["address"] = address, ["size"] = size })["hex"]!;

		[DbgFact]
		public void Written_bytes_read_back_identically() {
			var address = Hex(ScratchAddress());

			var result = Write(address, "0102030405060708");

			Assert.Contains("verified 8", result);
			Assert.Equal("0102030405060708", ReadHex(address, 8));
		}

		// Reading back the same bytes only proves the address is writable memory. That it is the
		// fixture's array is a separate claim, and the debugger's own view of Scratch[0] settles it.
		[DbgFact]
		public void A_write_lands_in_the_managed_array_its_address_came_from() {
			Write(Hex(ScratchAddress()), "2A");

			var res = Dbg.CallJson("dbg_eval", new JObject { ["expression"] = "DbgTest.Program.Scratch[0]" });
			Assert.Equal(0x2A, Dbg.Number(res["value"]));
		}

		// The failure path of the tool's own verification. The bytes must be non-zero: an unreadable
		// address reads back as zeros (the read API reports no failure either), so a write of zeros
		// would confirm itself no matter what happened.
		[DbgFact]
		public void A_write_that_cannot_land_is_reported_as_unconfirmed() {
			var error = Dbg.CallExpectingError("dbg_write_memory", new JObject {
				["address"] = UnwritableAddress,
				["bytes"] = "DEADBEEF",
			});

			Assert.Contains("not confirmed", error, StringComparison.OrdinalIgnoreCase);
		}

		// Each spelling gets its own offset in the 16-byte array. A fresh debuggee starts it zeroed,
		// so a spelling that was parsed into nothing would read back as 0000 rather than inheriting a
		// previous write's bytes and passing.
		[DbgFact]
		public void Every_hex_spelling_of_the_same_bytes_is_accepted() {
			var scratch = ScratchAddress();

			Write(Hex(scratch), "DEAD");
			Write(Hex(scratch + 2), "0xDEAD");
			Write(Hex(scratch + 4), "DE AD");
			Write(Hex(scratch + 6), "0x90 0x90");

			Assert.Equal("DEAD", ReadHex(Hex(scratch), 2));
			Assert.Equal("DEAD", ReadHex(Hex(scratch + 2), 2));
			Assert.Equal("DEAD", ReadHex(Hex(scratch + 4), 2));
			Assert.Equal("9090", ReadHex(Hex(scratch + 6), 2));
		}

		// Bad bytes are rejected while parsing, before any memory is touched, so the address only has
		// to parse — using an unwritable one keeps the test from depending on the scratch buffer.
		[DbgFact]
		public void Malformed_hex_bytes_are_rejected() {
			var notHex = Dbg.CallExpectingError("dbg_write_memory", new JObject {
				["address"] = UnwritableAddress,
				["bytes"] = "ZZ",
			});
			Assert.Contains("invalid hex", notHex, StringComparison.OrdinalIgnoreCase);

			var oddLength = Dbg.CallExpectingError("dbg_write_memory", new JObject {
				["address"] = UnwritableAddress,
				["bytes"] = "ABC",
			});
			Assert.Contains("even number", oddLength, StringComparison.OrdinalIgnoreCase);
		}

		// An empty string parses into zero bytes rather than failing, so the tool has to reject it
		// itself — otherwise it would report a successful write of nothing.
		[DbgFact]
		public void An_empty_byte_string_is_rejected() {
			var error = Dbg.CallExpectingError("dbg_write_memory", new JObject {
				["address"] = UnwritableAddress,
				["bytes"] = "",
			});

			Assert.Contains("empty", error, StringComparison.OrdinalIgnoreCase);
		}

		[DbgFact]
		public void Addresses_are_accepted_in_hex_and_in_decimal() {
			var scratch = ScratchAddress();
			var hex = Hex(scratch);
			var dec = scratch.ToString(CultureInfo.InvariantCulture);

			Write(dec, "5A");

			// One address, two spellings: the hex read has to see what the decimal write left, and
			// both reads have to report the same resolved address back.
			var viaHex = Dbg.CallJson("dbg_read_memory", new JObject { ["address"] = hex, ["size"] = 1 });
			var viaDec = Dbg.CallJson("dbg_read_memory", new JObject { ["address"] = dec, ["size"] = 1 });

			Assert.Equal("5A", (string?)viaHex["hex"]);
			Assert.Equal("5A", (string?)viaDec["hex"]);
			Assert.Equal(hex, (string?)viaHex["address"]);
			Assert.Equal(hex, (string?)viaDec["address"]);
		}
	}
}
