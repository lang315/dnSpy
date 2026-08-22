using System;
using System.IO;
using dnSpy.MCP.Server;
using dnSpy.MCP.Tools;
using Xunit;

namespace dnSpy.MCP.Tests {
	/// <summary>
	/// Token resolution. The headline property is that the token SURVIVES A RESTART: a token that
	/// changed on every launch would force the MCP client to be reconfigured each time, which is the
	/// reason authentication used to be opt-in.
	/// </summary>
	public class TokenStoreTests : IDisposable {
		const string TokenEnv = "DNSPY_MCP_TOKEN";
		const string NoAuthEnv = "DNSPY_MCP_NO_AUTH";

		readonly string dir;
		readonly string settingsFile;
		readonly string? savedToken;
		readonly string? savedNoAuth;

		public TokenStoreTests() {
			savedToken = Environment.GetEnvironmentVariable(TokenEnv);
			savedNoAuth = Environment.GetEnvironmentVariable(NoAuthEnv);
			Environment.SetEnvironmentVariable(TokenEnv, null);
			Environment.SetEnvironmentVariable(NoAuthEnv, null);

			dir = Path.Combine(Path.GetTempPath(), "dnSpy.MCP.tokentest." + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(dir);
			settingsFile = Path.Combine(dir, "dnSpy.xml");
		}

		public void Dispose() {
			Environment.SetEnvironmentVariable(TokenEnv, savedToken);
			Environment.SetEnvironmentVariable(NoAuthEnv, savedNoAuth);
			try { Directory.Delete(dir, recursive: true); } catch (Exception) { }
		}

		static void Ignore(string message) { }

		[Fact]
		public void A_first_run_generates_a_token_and_writes_it_next_to_the_settings_file() {
			var resolved = TokenStore.Resolve(settingsFile, Ignore);

			Assert.Equal(InfoTools.AuthSourceFile, resolved.Source);
			Assert.False(string.IsNullOrEmpty(resolved.Token));
			Assert.Equal(Path.Combine(dir, "mcp-token.txt"), resolved.FilePath);
			Assert.True(File.Exists(resolved.FilePath));
			Assert.Contains(resolved.Token!, File.ReadAllText(resolved.FilePath!));
		}

		// The whole point of persisting: reconfiguring the MCP client on every launch would be worse
		// than the problem authentication solves.
		[Fact]
		public void A_second_run_reuses_the_same_token() {
			var first = TokenStore.Resolve(settingsFile, Ignore);
			var second = TokenStore.Resolve(settingsFile, Ignore);

			Assert.Equal(first.Token, second.Token);
			Assert.Equal(InfoTools.AuthSourceFile, second.Source);
		}

		// Anchoring to the settings file is what makes --settings-file isolate the token as well as
		// the breakpoints, so a throwaway dnSpy cannot read the real one.
		[Fact]
		public void A_different_settings_file_gets_a_different_token() {
			var otherDir = Path.Combine(dir, "other");
			Directory.CreateDirectory(otherDir);

			var mine = TokenStore.Resolve(settingsFile, Ignore);
			var theirs = TokenStore.Resolve(Path.Combine(otherDir, "dnSpy.xml"), Ignore);

			Assert.NotEqual(mine.Token, theirs.Token);
		}

		[Fact]
		public void The_environment_variable_wins_over_the_file() {
			TokenStore.Resolve(settingsFile, Ignore); // create the file first
			Environment.SetEnvironmentVariable(TokenEnv, "explicit-token");

			var resolved = TokenStore.Resolve(settingsFile, Ignore);

			Assert.Equal("explicit-token", resolved.Token);
			Assert.Equal(InfoTools.AuthSourceEnv, resolved.Source);
		}

		[Theory]
		[InlineData("1")]
		[InlineData("true")]
		[InlineData("TRUE")]
		public void Authentication_can_be_turned_off_deliberately(string value) {
			Environment.SetEnvironmentVariable(NoAuthEnv, value);

			var resolved = TokenStore.Resolve(settingsFile, Ignore);

			Assert.Null(resolved.Token);
			Assert.Equal(InfoTools.AuthSourceNone, resolved.Source);
			Assert.False(File.Exists(Path.Combine(dir, "mcp-token.txt")),
				"opting out should not leave a token file behind");
		}

		[Fact]
		public void Turning_authentication_off_is_logged() {
			Environment.SetEnvironmentVariable(NoAuthEnv, "1");
			var logged = "";

			TokenStore.Resolve(settingsFile, m => logged += m);

			Assert.Contains("WITHOUT authentication", logged);
		}

		// An unrecognised value must not be read as consent to run open.
		[Theory]
		[InlineData("0")]
		[InlineData("false")]
		[InlineData("yes")]
		[InlineData("")]
		public void An_ambiguous_opt_out_value_keeps_authentication_on(string value) {
			Environment.SetEnvironmentVariable(NoAuthEnv, value);

			var resolved = TokenStore.Resolve(settingsFile, Ignore);

			Assert.NotNull(resolved.Token);
		}

		[Fact]
		public void An_empty_token_file_is_replaced_rather_than_trusted() {
			var path = Path.Combine(dir, "mcp-token.txt");
			File.WriteAllText(path, "   \r\n");

			var resolved = TokenStore.Resolve(settingsFile, Ignore);

			Assert.False(string.IsNullOrWhiteSpace(resolved.Token));
			Assert.Contains(resolved.Token!, File.ReadAllText(path));
		}

		[Fact]
		public void A_generated_token_is_long_and_url_safe() {
			var token = TokenStore.Resolve(settingsFile, Ignore).Token!;

			// 32 random bytes, base64url, padding stripped.
			Assert.Equal(43, token.Length);
			Assert.DoesNotContain('+', token);
			Assert.DoesNotContain('/', token);
			Assert.DoesNotContain('=', token);
		}

		// Failing open would be the wrong answer to "the disk said no".
		[Fact]
		public void A_token_that_cannot_be_persisted_still_protects_the_endpoint() {
			var unwritable = Path.Combine(dir, "nope.xml");
			File.WriteAllText(Path.Combine(dir, "mcp-token.txt"), ""); // empty => must regenerate
			using (var hold = new FileStream(Path.Combine(dir, "mcp-token.txt"),
				FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
				var logged = "";
				var resolved = TokenStore.Resolve(unwritable, m => logged += m);

				Assert.NotNull(resolved.Token);
				Assert.Null(resolved.FilePath);
				Assert.Contains(resolved.Token!, logged);
			}
		}
	}
}
