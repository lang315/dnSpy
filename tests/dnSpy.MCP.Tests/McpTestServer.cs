using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using dnSpy.MCP.Server;
using dnSpy.MCP.Tools;
using Newtonsoft.Json.Linq;

namespace dnSpy.MCP.Tests {
	/// <summary>
	/// Runs a real <see cref="McpServer"/> on a free loopback port with stub tools, so the protocol
	/// and security layer can be exercised over actual HTTP without dnSpy, WPF or a debug engine.
	/// </summary>
	sealed class McpTestServer : IDisposable {
		readonly McpServer server;
		readonly ConcurrentQueue<string> logs = new();

		static int inFlightSlowCalls;

		public int Port { get; }
		public string BaseUrl => $"http://127.0.0.1:{Port}";
		public string McpUrl => BaseUrl + "/mcp";
		public IReadOnlyCollection<string> Logs => logs;

		/// <summary>How many stub_slow handlers are currently blocked inside the server.</summary>
		public int InFlightSlowCalls => Volatile.Read(ref inFlightSlowCalls);

		public McpTestServer(string? authToken = null, IReadOnlyList<ToolDef>? tools = null,
			string? tokenFilePath = null) {
			Port = FreePort();
			server = new McpServer(Port, tools ?? StubTools(), logs.Enqueue, authToken, tokenFilePath);
			server.Start();
		}

		public void Broadcast(string method, JObject @params) => server.Broadcast(method, @params);

		public bool LoggedContaining(string fragment) {
			foreach (var line in logs) {
				if (line.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
					return true;
			}
			return false;
		}

		// Ask the OS for an unused port, then hand it to HttpListener. There is a small race between
		// releasing it and rebinding, but it keeps tests independent and lets them run in any order.
		static int FreePort() {
			var probe = new TcpListener(IPAddress.Loopback, 0);
			probe.Start();
			var port = ((IPEndPoint)probe.LocalEndpoint).Port;
			probe.Stop();
			return port;
		}

		/// <summary>The tool surface used by most tests: one of every annotation and failure shape.</summary>
		public static IReadOnlyList<ToolDef> StubTools() => new[] {
			new ToolDef("stub_read", "Read-only stub that echoes its arguments.",
				Schema.Object(("text", Schema.Str("Anything"), false)),
				args => args.ToString(Newtonsoft.Json.Formatting.None), readOnly: true),

			new ToolDef("stub_destroy", "Destructive stub.",
				Schema.Object(("target", Schema.Str("What to destroy"), true)),
				_ => "destroyed", destructive: true),

			new ToolDef("stub_plain", "Stub with neither annotation.",
				Schema.Object(),
				_ => "plain"),

			new ToolDef("stub_throw", "Stub that always throws.",
				Schema.Object(),
				_ => throw new InvalidOperationException("stub failure message")),

			new ToolDef("stub_slow", "Stub that blocks, to model a long-poll tool.",
				Schema.Object(("ms", Schema.Int("How long to block"), false)),
				args => {
					Interlocked.Increment(ref inFlightSlowCalls);
					try {
						Thread.Sleep((int?)args["ms"] ?? 30000);
						return "slept";
					}
					finally {
						Interlocked.Decrement(ref inFlightSlowCalls);
					}
				}),
		};

		public void Dispose() => server.Stop();
	}
}
