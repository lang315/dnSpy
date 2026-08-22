using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace dnSpy.MCP.IntegrationTests {
	/// <summary>
	/// The decrypt_strings tool: find a decryptor method's call sites, read the constant argument at
	/// each, and — when a session is paused — func-eval the decryptor to recover the plaintext.
	///
	/// Anchored to the fixture's Program.Dec (Program.cs), a single-byte-XOR string decryptor whose
	/// plaintext never appears literally in the assembly: Dec(1) == "secret-one", Dec(2) == "secret-two",
	/// called with those constant ids from Program.UseSecrets.
	///
	/// Every test fails before decrypt_strings is registered in DecryptTools.Create(): the server reports
	/// an unknown tool (DbgToolException), so CallJson throws and CallExpectingError's "unknown tool"
	/// message would not contain the strings the error tests assert on.
	/// </summary>
	[Collection("dnSpy")]
	public class DecryptStringsIntegrationTests : IDisposable {
		public DecryptStringsIntegrationTests() => Dbg.Reset();
		public void Dispose() => Dbg.Reset();

		const string Decryptor = "DbgTest.Program.Dec";

		// Static path (no session needed): dry_run reads the two call sites and their constant int ids
		// without touching the debugger. Fails pre-impl: the tool does not exist, so CallJson throws.
		[DbgFact]
		public void Dry_run_lists_the_call_sites_and_distinct_int_args() {
			var res = Dbg.CallJson("decrypt_strings", new JObject {
				["module"] = Dbg.FixtureDll(),
				["method"] = Decryptor,
				["dry_run"] = true,
			});

			// The decryptor is echoed with its 0x06 method token.
			Assert.Contains("Dec", (string?)res["decryptor"]);
			Assert.StartsWith("0x06", (string?)res["decryptorToken"]);

			// Both call sites in UseSecrets are found, each with an int constant argument (1 and 2).
			var callSites = (JArray)res["callSites"]!;
			Assert.Contains(callSites, c => (int?)c["arg"] == 1 && (string?)c["argKind"] == "int");
			Assert.Contains(callSites, c => (int?)c["arg"] == 2 && (string?)c["argKind"] == "int");
			Assert.All(callSites, c => Assert.StartsWith("0x06", (string?)c["methodToken"]));
			Assert.All(callSites, c => Assert.StartsWith("0x", (string?)c["ilOffset"]));
			Assert.Contains(callSites, c => ((string?)c["method"])?.Contains("UseSecrets") == true);

			// dry_run performs no func-eval: no plaintext, and a note says a paused session is needed.
			Assert.Empty((JArray)res["results"]!);
			Assert.Equal(0, (int?)res["evaluated"]);
			Assert.False(string.IsNullOrEmpty((string?)res["note"]));
		}

		// Without a paused session (and without dry_run) the tool must still succeed: it returns the
		// static call sites and a note rather than erroring. Fails pre-impl (unknown tool -> throw).
		[DbgFact]
		public void Without_a_session_the_call_sites_are_returned_with_a_note() {
			Assert.False((bool)Dbg.Status()["isDebugging"]!, "precondition: nothing is debugging");

			var res = Dbg.CallJson("decrypt_strings", new JObject {
				["module"] = Dbg.FixtureDll(),
				["method"] = Decryptor,
			});

			Assert.NotEmpty((JArray)res["callSites"]!);
			Assert.Empty((JArray)res["results"]!);
			Assert.Equal(0, (int?)res["evaluated"]);
			// The note tells the caller why there is no plaintext.
			Assert.Contains("session", (string?)res["note"], StringComparison.OrdinalIgnoreCase);
		}

		// The headline behaviour: paused, the tool func-evals the decryptor once per distinct id and
		// recovers the plaintext neither call site nor the assembly reveals statically. Fails pre-impl.
		[DbgFact]
		public void A_paused_session_decrypts_each_distinct_constant() {
			PauseInsideAdd();

			var res = Dbg.CallJson("decrypt_strings", new JObject {
				["module"] = Dbg.FixtureDll(),
				["method"] = Decryptor,
			});

			var results = (JArray)res["results"]!;
			// Two distinct ids -> two func-evals -> the two plaintexts.
			Assert.Equal(2, (int?)res["evaluated"]);
			Assert.Contains("secret-one", Plaintext(results, 1));
			Assert.Contains("secret-two", Plaintext(results, 2));
			// Every result carries the id it decrypts and its kind.
			Assert.All(results, r => Assert.Equal("int", (string?)r["argKind"]));
		}

		// An instance decryptor is out of scope for v1 and must be rejected with a clear message, not a
		// confusing eval failure. Fails pre-impl: the tool does not exist, so the error is "unknown tool".
		[DbgFact]
		public void An_instance_decryptor_is_rejected_as_unsupported() {
			// Animal.Speak is a virtual instance method — resolvable, but not static.
			var error = Dbg.CallExpectingError("decrypt_strings", new JObject {
				["module"] = Dbg.FixtureDll(),
				["method"] = "DbgTest.Animal.Speak",
			});
			Assert.Contains("instance", error, StringComparison.OrdinalIgnoreCase);
		}

		// An unknown decryptor name is reported by name. Fails pre-impl: the error would be the generic
		// "unknown tool" message, which does not contain the missing method name.
		[DbgFact]
		public void An_unknown_decryptor_method_is_reported() {
			var error = Dbg.CallExpectingError("decrypt_strings", new JObject {
				["module"] = Dbg.FixtureDll(),
				["method"] = "DbgTest.Program.NoSuchDecryptor",
			});
			Assert.Contains("NoSuchDecryptor", error);
		}

		/// <summary>Reads the recovered plaintext for a given int id out of the results array.</summary>
		static string Plaintext(JArray results, int arg) {
			var hit = results.FirstOrDefault(r => (int?)r["arg"] == arg && (string?)r["argKind"] == "int");
			Assert.True(hit is not null, $"expected a result for id {arg}");
			return (string?)hit!["plaintext"] ?? "";
		}

		/// <summary>
		/// Stops the fixture inside Program.Add (a == 7), a proven-stable pause point reached well after
		/// Warmup has run — so the decryptor's type is initialised and a readable frame exists for the
		/// func-eval. Mirrors InspectionIntegrationTests.PauseInsideAdd.
		/// </summary>
		static void PauseInsideAdd() {
			Dbg.Call("bp_add", new JObject {
				["module"] = Dbg.FixtureDll(),
				["token"] = Dbg.TokenOf("DbgTest.Program.Add"),
				["condition"] = "a == 7",
			});
			Dbg.Call("dbg_start", new JObject { ["path"] = Dbg.FixtureDll() });
			Dbg.WaitForBreak();
		}
	}
}
