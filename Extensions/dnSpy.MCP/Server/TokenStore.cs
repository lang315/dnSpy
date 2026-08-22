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
using System.IO;
using System.Security.Cryptography;
using System.Text;
using dnSpy.MCP.Tools;

namespace dnSpy.MCP.Server {
	/// <summary>Where the bearer token came from, and the token itself if there is one.</summary>
	sealed class ResolvedToken {
		/// <summary>Null only when authentication is disabled outright.</summary>
		public string? Token { get; }
		/// <summary>One of the <c>InfoTools.AuthSource*</c> values, reported by <c>dnspy_info</c>.</summary>
		public string Source { get; }
		/// <summary>Where a caller can read the token, or null if it never reached disk.</summary>
		public string? FilePath { get; }

		public ResolvedToken(string? token, string source, string? filePath) {
			Token = token;
			Source = source;
			FilePath = filePath;
		}
	}

	/// <summary>
	/// Resolves the bearer token the endpoint requires. Authentication is on by default: the tools
	/// launch processes, patch memory and evaluate expressions inside a debuggee, so an unauthenticated
	/// loopback port is local code execution for anything running as the user.
	///
	/// The token is generated once and persisted so it survives restarts — a token that changed on
	/// every launch would force the MCP client to be reconfigured each time, which is why this was
	/// opt-in before.
	///
	/// It lives next to the settings file rather than at a fixed %APPDATA% path so that it follows
	/// <c>--settings-file</c>: an isolated dnSpy gets its own token and cannot read the real one.
	/// </summary>
	static class TokenStore {
		const string TokenFileName = "mcp-token.txt";
		const string TokenEnvVar = "DNSPY_MCP_TOKEN";
		const string NoAuthEnvVar = "DNSPY_MCP_NO_AUTH";

		public static ResolvedToken Resolve(string settingsFilename, Action<string> log) {
			if (IsTruthy(Environment.GetEnvironmentVariable(NoAuthEnvVar))) {
				log($"warning: {NoAuthEnvVar} is set — running WITHOUT authentication. The endpoint is " +
					"loopback-only, but any local process running as you can drive the debugger.");
				return new ResolvedToken(null, InfoTools.AuthSourceNone, null);
			}

			var fromEnv = Environment.GetEnvironmentVariable(TokenEnvVar);
			if (!string.IsNullOrEmpty(fromEnv))
				return new ResolvedToken(fromEnv, InfoTools.AuthSourceEnv, null);

			var path = TokenFilePath(settingsFilename);
			if (path is null) {
				// No settings directory to anchor to; still refuse to run open.
				var ephemeral = Generate();
				log("warning: could not determine a settings directory for the token file. This " +
					$"session's token is: {ephemeral}");
				return new ResolvedToken(ephemeral, InfoTools.AuthSourceFile, null);
			}

			var existing = TryRead(path);
			if (existing is not null)
				return new ResolvedToken(existing, InfoTools.AuthSourceFile, path);

			var token = Generate();
			if (TryWrite(path, token, log)) {
				log($"generated a new MCP bearer token at {path}");
				return new ResolvedToken(token, InfoTools.AuthSourceFile, path);
			}

			// Persisting failed, but failing open would be worse than being inconvenient. Put the token
			// in the log — same directory protection as the file would have had — and keep auth on.
			log($"warning: could not write the token file at {path}. This session's token is: {token}");
			return new ResolvedToken(token, InfoTools.AuthSourceFile, null);
		}

		/// <summary>The token file that belongs to a given settings file, or null if it has no directory.</summary>
		public static string? TokenFilePath(string settingsFilename) {
			try {
				var dir = Path.GetDirectoryName(Path.GetFullPath(settingsFilename));
				return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir!, TokenFileName);
			}
			catch (Exception) {
				return null;
			}
		}

		static string? TryRead(string path) {
			try {
				if (!File.Exists(path))
					return null;
				var text = File.ReadAllText(path).Trim();
				return text.Length == 0 ? null : text;
			}
			catch (Exception) {
				return null;
			}
		}

		static bool TryWrite(string path, string token, Action<string> log) {
			try {
				var dir = Path.GetDirectoryName(path)!;
				Directory.CreateDirectory(dir);
				File.WriteAllText(path, token + Environment.NewLine, new UTF8Encoding(false));
				// No explicit ACL: %APPDATA% and the per-user %TEMP% are already user-private, the same
				// protection dnSpy.xml itself relies on. Anywhere else is the caller's choice to explain.
				return true;
			}
			catch (Exception ex) {
				log($"token file write failed: {ex.Message}");
				return false;
			}
		}

		static string Generate() {
			var bytes = new byte[32];
			using (var rng = RandomNumberGenerator.Create())
				rng.GetBytes(bytes);
			// base64url: safe to paste into a header, a shell command or a JSON config unescaped.
			return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
		}

		static bool IsTruthy(string? value) =>
			string.Equals(value, "1", StringComparison.Ordinal) ||
			string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
	}
}
