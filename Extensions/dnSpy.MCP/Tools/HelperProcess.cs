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
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace dnSpy.MCP.Tools {
	/// <summary>
	/// Shared plumbing for the tools that shell out to a bundled helper executable published next to this
	/// extension (heap_stats/find/object via HeapHelper, deobfuscate via DeobHost): locate the exe, spawn it
	/// with correctly quoted arguments, capture stdout/stderr under a timeout, and parse its single JSON
	/// object. Each tool keeps its own result envelope — this only gets the JSON back.
	/// </summary>
	static class HelperProcess {
		/// <summary>
		/// Locate a helper executable at <paramref name="relativePath"/> (e.g. <c>HeapHelper\win-x64\x.exe</c>)
		/// next to this extension. Probes the extension's own directory first, then one directory up (covers
		/// the build.ps1 layout where the extension DLL and the helper folder move together), then
		/// AppContext.BaseDirectory. Throws with <paramref name="notFoundHint"/> appended when nothing matches.
		/// </summary>
		public static string Locate(string relativePath, string notFoundHint) {
			var bases = ProbeDirectories();
			foreach (var b in bases) {
				var candidate = Path.Combine(b, relativePath);
				if (File.Exists(candidate))
					return candidate;
			}

			var expected = bases.Count > 0 ? Path.Combine(bases[0], relativePath) : relativePath;
			throw new InvalidOperationException(
				$"{Path.GetFileName(relativePath)} was not found next to the extension (expected under {Path.GetDirectoryName(relativePath)}). " +
				notFoundHint + $" Expected path: {expected}");
		}

		static List<string> ProbeDirectories() {
			var bases = new List<string>();
			var loc = Assembly.GetExecutingAssembly().Location;
			if (!string.IsNullOrEmpty(loc)) {
				var dir = Path.GetDirectoryName(loc);
				if (!string.IsNullOrEmpty(dir)) {
					bases.Add(dir!);
					var parent = Path.GetDirectoryName(dir);
					if (!string.IsNullOrEmpty(parent))
						bases.Add(parent!);
				}
			}
			var baseDir = AppContext.BaseDirectory;
			if (!string.IsNullOrEmpty(baseDir))
				bases.Add(baseDir);
			return bases;
		}

		/// <summary>
		/// Run <paramref name="exe"/> with stdout/stderr redirected and no window, on the calling (request)
		/// thread — never the debugger dispatcher. stdout is read to completion and parsed as one JSON object;
		/// the process is killed if it overruns <paramref name="timeoutMs"/>. <paramref name="displayName"/>
		/// names the helper in error messages and <paramref name="timeoutHint"/> explains a likely cause of a
		/// timeout to the caller.
		/// </summary>
		public static JObject Run(string exe, List<string> argv, int timeoutMs, string displayName, string timeoutHint) {
			var (exitCode, stdout, stderr) = Spawn(exe, argv, timeoutMs, displayName, timeoutHint);
			try {
				return JObject.Parse(stdout);
			}
			catch (Exception ex) {
				var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
				throw new InvalidOperationException($"{displayName} did not return valid JSON (exit {exitCode}): {Trim(detail)} [{ex.Message}]");
			}
		}

		static (int exitCode, string stdout, string stderr) Spawn(string exe, List<string> argv, int timeoutMs, string displayName, string timeoutHint) {
			var psi = new ProcessStartInfo {
				FileName = exe,
				Arguments = BuildArguments(argv),
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
				WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory,
				StandardOutputEncoding = Encoding.UTF8,
				StandardErrorEncoding = Encoding.UTF8,
			};

			using var proc = new Process { StartInfo = psi };
			try {
				proc.Start();
			}
			catch (Exception ex) {
				throw new InvalidOperationException($"could not start {displayName} ({exe}): {ex.Message}");
			}

			// Read both streams asynchronously before waiting, so a large payload cannot deadlock on a full
			// pipe buffer.
			var outTask = proc.StandardOutput.ReadToEndAsync();
			var errTask = proc.StandardError.ReadToEndAsync();

			if (!proc.WaitForExit(timeoutMs)) {
				try { proc.Kill(); } catch { /* already gone */ }
				throw new InvalidOperationException($"{displayName} did not finish within {timeoutMs / 1000} s and was terminated ({timeoutHint})");
			}
			// The timed overload can return before the redirected streams have flushed; block for the rest.
			proc.WaitForExit();

			return (proc.ExitCode, SafeResult(outTask), SafeResult(errTask));
		}

		static string SafeResult(Task<string> task) {
			try {
				return task.GetAwaiter().GetResult() ?? string.Empty;
			}
			catch {
				return string.Empty;
			}
		}

		// Quote arguments per the Windows CommandLineToArgvW rules so a path or type name with spaces, quotes
		// or trailing backslashes round-trips. (net48 has no ProcessStartInfo.ArgumentList.)
		static string BuildArguments(List<string> argv) {
			var sb = new StringBuilder();
			foreach (var a in argv) {
				if (sb.Length > 0)
					sb.Append(' ');
				AppendArgument(sb, a);
			}
			return sb.ToString();
		}

		static void AppendArgument(StringBuilder sb, string arg) {
			if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) {
				sb.Append(arg);
				return;
			}
			sb.Append('"');
			for (int i = 0; ; i++) {
				var backslashes = 0;
				while (i < arg.Length && arg[i] == '\\') {
					i++;
					backslashes++;
				}
				if (i == arg.Length) {
					// Escape all trailing backslashes so the closing quote is not consumed.
					sb.Append('\\', backslashes * 2);
					break;
				}
				if (arg[i] == '"') {
					// Escape the backslashes preceding the quote, then the quote itself.
					sb.Append('\\', backslashes * 2 + 1);
					sb.Append('"');
				}
				else {
					sb.Append('\\', backslashes);
					sb.Append(arg[i]);
				}
			}
			sb.Append('"');
		}

		static string Trim(string s) {
			s = s.Trim();
			return s.Length <= 500 ? s : s.Substring(0, 500) + "…";
		}
	}
}
