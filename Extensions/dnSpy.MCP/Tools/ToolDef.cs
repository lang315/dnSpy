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
using Newtonsoft.Json.Linq;

namespace dnSpy.MCP.Tools {
	/// <summary>
	/// One MCP tool: its name, human description, JSON-schema for its arguments, and a handler
	/// that receives the parsed arguments and returns a text result (usually JSON).
	/// </summary>
	sealed class ToolDef {
		public string Name { get; }
		public string Description { get; }
		public JObject InputSchema { get; }
		public Func<JObject, string> Handler { get; }
		/// <summary>Tool only reads state (MCP <c>readOnlyHint</c>).</summary>
		public bool ReadOnly { get; }
		/// <summary>Tool may change program/debugger state destructively (MCP <c>destructiveHint</c>).</summary>
		public bool Destructive { get; }

		public ToolDef(string name, string description, JObject inputSchema, Func<JObject, string> handler,
			bool readOnly = false, bool destructive = false) {
			Name = name;
			Description = description;
			InputSchema = inputSchema;
			Handler = handler;
			ReadOnly = readOnly;
			Destructive = destructive;
		}
	}

	/// <summary>Helpers for building JSON-schema fragments without hand-writing JObject trees.</summary>
	static class Schema {
		public static JObject Object(params (string name, JObject prop, bool required)[] props) {
			var properties = new JObject();
			var required = new JArray();
			foreach (var (name, prop, req) in props) {
				properties[name] = prop;
				if (req)
					required.Add(name);
			}
			var obj = new JObject {
				["type"] = "object",
				["properties"] = properties,
			};
			if (required.Count > 0)
				obj["required"] = required;
			return obj;
		}

		public static JObject Str(string description) => new JObject { ["type"] = "string", ["description"] = description };
		public static JObject Int(string description) => new JObject { ["type"] = "integer", ["description"] = description };
		public static JObject Bool(string description) => new JObject { ["type"] = "boolean", ["description"] = description };
	}
}
