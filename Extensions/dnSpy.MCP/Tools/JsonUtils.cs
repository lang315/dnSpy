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

using System.Linq;
using dnSpy.Contracts.Debugger;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace dnSpy.MCP.Tools {
	/// <summary>Serialization helpers shared by the tool handlers.</summary>
	static class JsonUtils {
		public static string Json(JToken token) => token.ToString(Formatting.Indented);

		public static JObject Process(DbgProcess p) => new JObject {
			["id"] = p.Id,
			["name"] = p.Name,
			["filename"] = p.Filename,
			["bitness"] = p.Bitness,
			["state"] = p.State.ToString(),
			["isRunning"] = p.IsRunning,
		};

		public static JObject ThreadJson(DbgThread t) => new JObject {
			["id"] = t.Id,
			["managedId"] = t.ManagedId is null ? null : (long)t.ManagedId.Value,
			["name"] = t.HasName() ? t.Name : t.UIName,
			["kind"] = t.Kind,
			["state"] = string.Join(", ", t.State.Select(s => s.State)),
		};

		public static JObject Module(DbgModule m) => new JObject {
			["name"] = m.Name,
			["filename"] = m.Filename,
			["address"] = m.HasAddress ? "0x" + m.Address.ToString("X") : null,
			["size"] = m.Size,
			["isDynamic"] = m.IsDynamic,
			["isInMemory"] = m.IsInMemory,
			["order"] = m.Order,
		};
	}
}
