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

using Newtonsoft.Json.Linq;

namespace dnSpy.MCP.Server {
	/// <summary>JSON-RPC 2.0 response/error builders + standard error codes.</summary>
	static class JsonRpc {
		public const int ParseError = -32700;
		public const int InvalidRequest = -32600;
		public const int MethodNotFound = -32601;
		public const int InvalidParams = -32602;
		public const int InternalError = -32603;

		public static JObject Result(JToken id, JObject result) => new JObject {
			["jsonrpc"] = "2.0",
			["id"] = id,
			["result"] = result,
		};

		public static JObject Error(JToken? id, int code, string message) => new JObject {
			["jsonrpc"] = "2.0",
			["id"] = id ?? JValue.CreateNull(),
			["error"] = new JObject {
				["code"] = code,
				["message"] = message,
			},
		};
	}
}
