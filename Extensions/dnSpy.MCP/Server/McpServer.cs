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
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using dnSpy.MCP.Tools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// ponytail: hand-rolled MCP-over-HTTP. No MCP SDK dependency (none targets net48 cleanly);
// the protocol surface we need is tiny (initialize / tools/list / tools/call).

namespace dnSpy.MCP.Server {
	/// <summary>
	/// Minimal MCP server over Streamable-HTTP (stateless JSON mode). Handles a single JSON-RPC
	/// message per POST to <c>/mcp</c> and answers with <c>application/json</c>. Bound to loopback
	/// only. Implements just the methods an MCP tool client needs: initialize, tools/list, tools/call.
	/// </summary>
	sealed class McpServer {
		const string ProtocolVersion = "2024-11-05";
		const string ServerName = "dnSpy";
		const string McpPath = "/mcp";
		const long MaxBodyBytes = 4 * 1024 * 1024;

		readonly HttpListener listener;
		readonly IReadOnlyDictionary<string, ToolDef> tools;
		readonly IReadOnlyList<ToolDef> toolList;
		readonly Action<string> log;
		readonly string? authToken;
		Thread? acceptThread;
		volatile bool running;

		public int Port { get; }

		/// <param name="authToken">If non-null, requests must send <c>Authorization: Bearer &lt;token&gt;</c>.</param>
		public McpServer(int port, IReadOnlyList<ToolDef> tools, Action<string> log, string? authToken = null) {
			Port = port;
			this.log = log;
			this.authToken = string.IsNullOrEmpty(authToken) ? null : authToken;
			toolList = tools;
			var map = new Dictionary<string, ToolDef>(StringComparer.Ordinal);
			foreach (var t in tools)
				map[t.Name] = t;
			this.tools = map;
			listener = new HttpListener();
			listener.Prefixes.Add($"http://127.0.0.1:{port}/");
		}

		public void Start() {
			listener.Start();
			running = true;
			acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "dnSpy.MCP" };
			acceptThread.Start();
			log($"MCP server listening on http://127.0.0.1:{Port}/mcp ({toolList.Count} tools)");
		}

		public void Stop() {
			running = false;
			try { listener.Stop(); } catch { }
			try { listener.Close(); } catch { }
		}

		void AcceptLoop() {
			while (running) {
				HttpListenerContext ctx;
				try {
					ctx = listener.GetContext();
				}
				catch (Exception) {
					if (running)
						continue;
					break;
				}
				// Handle off the accept thread: long-poll tools (dbg_wait_for_break, dbg_step) must
				// not block other requests. DbgAccess serializes all debugger reads onto one thread.
				ThreadPool.QueueUserWorkItem(_ => {
					try {
						Handle(ctx);
					}
					catch (Exception ex) {
						log($"MCP request failed: {ex.Message}");
						try { ctx.Response.Abort(); } catch { }
					}
				});
			}
		}

		void Handle(HttpListenerContext ctx) {
			var req = ctx.Request;

			// Anti-CSRF / anti-DNS-rebinding: a real MCP client sends no browser Origin and a
			// loopback Host; a malicious web page cannot forge a 127.0.0.1 Host header, and browsers
			// always attach Origin on cross-origin requests. This closes the browser-pivot attack.
			// A local process running as the user is still trusted (same as running a local debugger).
			if (!IsLoopbackHost(req.Headers["Host"] ?? req.UserHostName) || !string.IsNullOrEmpty(req.Headers["Origin"])) {
				WriteText(ctx, 403, "text/plain", "forbidden");
				return;
			}
			if (!IsAuthorized(req)) {
				WriteText(ctx, 401, "text/plain", "unauthorized");
				return;
			}

			if (req.HttpMethod == "GET") {
				// No SSE stream in v1; a GET is only used for liveness checks.
				WriteText(ctx, 200, "text/plain", "dnSpy MCP server");
				return;
			}
			if (req.HttpMethod != "POST") {
				WriteText(ctx, 405, "text/plain", "method not allowed");
				return;
			}
			if (req.Url?.AbsolutePath != McpPath) {
				WriteText(ctx, 404, "text/plain", "not found");
				return;
			}
			if (req.ContentLength64 > MaxBodyBytes) {
				WriteText(ctx, 413, "text/plain", "request too large");
				return;
			}

			string body;
			using (var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
				body = reader.ReadToEnd();

			JObject request;
			try {
				request = JObject.Parse(body);
			}
			catch (Exception) {
				WriteJson(ctx, 400, JsonRpc.Error(null, JsonRpc.ParseError, "Parse error"));
				return;
			}

			var response = Dispatch(request);
			if (response is null) {
				// Notification: no response body.
				ctx.Response.StatusCode = 202;
				ctx.Response.Close();
				return;
			}
			WriteJson(ctx, 200, response);
		}

		JObject? Dispatch(JObject request) {
			var id = request["id"];
			var method = (string?)request["method"];
			var @params = request["params"] as JObject ?? new JObject();

			// Notifications have no id and expect no reply.
			if (id is null || id.Type == JTokenType.Null) {
				return null;
			}

			try {
				switch (method) {
				case "initialize":
					return JsonRpc.Result(id, Initialize());
				case "ping":
					return JsonRpc.Result(id, new JObject());
				case "tools/list":
					return JsonRpc.Result(id, ListTools());
				case "tools/call":
					return CallTool(id, @params);
				default:
					return JsonRpc.Error(id, JsonRpc.MethodNotFound, $"Method not found: {method}");
				}
			}
			catch (Exception ex) {
				return JsonRpc.Error(id, JsonRpc.InternalError, ex.Message);
			}
		}

		JObject Initialize() => new JObject {
			["protocolVersion"] = ProtocolVersion,
			["capabilities"] = new JObject {
				["tools"] = new JObject(),
			},
			["serverInfo"] = new JObject {
				["name"] = ServerName,
				["version"] = "1.0.0",
			},
		};

		JObject ListTools() {
			var arr = new JArray();
			foreach (var t in toolList) {
				arr.Add(new JObject {
					["name"] = t.Name,
					["description"] = t.Description,
					["inputSchema"] = t.InputSchema,
				});
			}
			return new JObject { ["tools"] = arr };
		}

		JObject CallTool(JToken id, JObject @params) {
			var name = (string?)@params["name"];
			if (name is null || !tools.TryGetValue(name, out var tool))
				return JsonRpc.Error(id, JsonRpc.InvalidParams, $"Unknown tool: {name}");

			var args = @params["arguments"] as JObject ?? new JObject();
			try {
				var text = tool.Handler(args);
				return JsonRpc.Result(id, ToolContent(text, isError: false));
			}
			catch (Exception ex) {
				return JsonRpc.Result(id, ToolContent(ex.Message, isError: true));
			}
		}

		bool IsLoopbackHost(string? host) =>
			host == $"127.0.0.1:{Port}" || host == $"localhost:{Port}" || host == $"[::1]:{Port}";

		bool IsAuthorized(HttpListenerRequest req) {
			if (authToken is null)
				return true;
			var header = req.Headers["Authorization"];
			const string prefix = "Bearer ";
			if (header is null || !header.StartsWith(prefix, StringComparison.Ordinal))
				return false;
			return FixedTimeEquals(header.Substring(prefix.Length), authToken);
		}

		static bool FixedTimeEquals(string a, string b) {
			// Constant-time compare so the token can't be recovered by timing.
			int diff = a.Length ^ b.Length;
			for (int i = 0; i < a.Length && i < b.Length; i++)
				diff |= a[i] ^ b[i];
			return diff == 0;
		}

		static JObject ToolContent(string text, bool isError) => new JObject {
			["content"] = new JArray {
				new JObject { ["type"] = "text", ["text"] = text },
			},
			["isError"] = isError,
		};

		static void WriteJson(HttpListenerContext ctx, int status, JObject payload) =>
			WriteText(ctx, status, "application/json", payload.ToString(Formatting.None));

		static void WriteText(HttpListenerContext ctx, int status, string contentType, string text) {
			var bytes = Encoding.UTF8.GetBytes(text);
			ctx.Response.StatusCode = status;
			ctx.Response.ContentType = contentType;
			ctx.Response.ContentLength64 = bytes.Length;
			ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
			ctx.Response.Close();
		}
	}
}
