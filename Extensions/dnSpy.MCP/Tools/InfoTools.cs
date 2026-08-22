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
using dnSpy.Contracts.App;
using Newtonsoft.Json.Linq;
using static dnSpy.MCP.Tools.JsonUtils;

namespace dnSpy.MCP.Tools {
	/// <summary>
	/// Identity and configuration of this endpoint. Deliberately depends on nothing but plain values
	/// and delegates — no debugger, MEF or WPF — so it answers even when the debug engine is dead,
	/// which is exactly when a caller needs to know which instance it reached.
	/// </summary>
	sealed class InfoTools {
		/// <summary>A bearer token was taken from the <c>DNSPY_MCP_TOKEN</c> environment variable.</summary>
		public const string AuthSourceEnv = "env";
		/// <summary>A bearer token was read from a file on disk.</summary>
		public const string AuthSourceFile = "file";
		/// <summary>No token — the endpoint accepts any loopback client.</summary>
		public const string AuthSourceNone = "none";

		// Mirrors the serverInfo McpServer answers initialize with; those consts are private to the
		// server and a caller comparing the two values must see them agree.
		const string ServerName = "dnSpy";
		const string ServerVersion = "1.0.0";

		readonly int port;
		readonly Func<bool> authRequired;
		readonly Func<string> authSource;
		readonly Func<int> toolCount;

		public InfoTools(int port, Func<bool> authRequired, Func<string> authSource, Func<int> toolCount) {
			this.port = port;
			this.authRequired = authRequired;
			this.authSource = authSource;
			this.toolCount = toolCount;
		}

		public IEnumerable<ToolDef> Create() {
			yield return new ToolDef("dnspy_info",
				"Report which dnSpy instance this endpoint belongs to and how it is configured: server name and version, dnSpy version, listening port, whether a bearer token is required and where it came from, and the settings file in use. Takes no arguments.",
				Schema.Object(), Info, readOnly: true);
		}

		string Info(JObject args) => Json(new JObject {
			["server"] = new JObject {
				["name"] = ServerName,
				["version"] = ServerVersion,
				// Late-bound: the host builds every tool before it can know the total, including this one.
				["toolCount"] = toolCount(),
			},
			["dnSpyVersion"] = DnSpyVersion,
			["port"] = port,
			["auth"] = new JObject {
				["required"] = authRequired(),
				["source"] = authSource(),
			},
			["settingsFile"] = AppDirectories.SettingsFilename,
		});

		// The extension is versioned with the rest of dnSpy, so its own assembly version is dnSpy's.
		// Read from this type's assembly rather than the entry assembly: the latter is null under a
		// test host, and Location-based lookups are empty for single-file publishes.
		static string DnSpyVersion => typeof(InfoTools).Assembly.GetName().Version?.ToString() ?? "unknown";
	}
}
