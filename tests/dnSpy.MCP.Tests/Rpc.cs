using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace dnSpy.MCP.Tests {
	/// <summary>An HTTP response captured as (status, body) plus the parsed JSON when there is any.</summary>
	sealed class RpcResponse {
		public HttpStatusCode Status { get; }
		public string Body { get; }

		public RpcResponse(HttpStatusCode status, string body) {
			Status = status;
			Body = body;
		}

		public JObject Json => JObject.Parse(Body);
		public JObject Result => (JObject)Json["result"]!;
		public JObject Error => (JObject)Json["error"]!;
		public int ErrorCode => (int)Error["code"]!;

		/// <summary>The text of a tools/call result, which MCP wraps in a content array.</summary>
		public string ToolText => (string)Result["content"]![0]!["text"]!;
		public bool ToolIsError => (bool)Result["isError"]!;
	}

	/// <summary>HTTP helpers for driving the MCP endpoint, including the shapes the guards care about.</summary>
	static class Rpc {
		static readonly HttpClient client = new(new SocketsHttpHandler {
			// Keep tests independent of pooled-connection reuse when a guard closes a connection early.
			PooledConnectionLifetime = TimeSpan.FromSeconds(5),
		}) { Timeout = TimeSpan.FromSeconds(100) };

		/// <summary>
		/// Runs before every request, given the target URL. The integration project sets this to its
		/// settings-file interlock so that even a test which calls Rpc directly — bypassing the Dbg
		/// helper — cannot reach a non-isolated dnSpy and delete real breakpoints. Tier 1 leaves it null.
		/// This is a per-assembly static: Rpc is a linked source file, so each test assembly has its own.
		/// </summary>
		public static Action<string>? RequestGuard;

		internal static void Guard(string url) => RequestGuard?.Invoke(url);

		public static RpcResponse Post(string url, JObject payload, string? token = null,
			string? origin = null, string? host = null) =>
			Send(url, HttpMethod.Post, payload.ToString(Newtonsoft.Json.Formatting.None), token, origin, host);

		public static RpcResponse PostRaw(string url, string body, string? token = null) =>
			Send(url, HttpMethod.Post, body, token, null, null);

		/// <summary>
		/// POSTs a body the server is expected to refuse mid-upload. Once the server has answered and
		/// closed, the rest of the upload can fail locally; that is the same rejection, so surface it
		/// as 413 instead of letting a transport error escape.
		/// </summary>
		public static RpcResponse PostRawTolerant(string url, string body) {
			try {
				return Send(url, HttpMethod.Post, body, null, null, null);
			}
			catch (HttpRequestException) {
				return new RpcResponse(HttpStatusCode.RequestEntityTooLarge, "<connection closed by server>");
			}
			catch (IOException) {
				return new RpcResponse(HttpStatusCode.RequestEntityTooLarge, "<connection closed by server>");
			}
		}

		/// <summary>Sends a verbatim Authorization header, so non-Bearer schemes can be exercised.</summary>
		public static RpcResponse PostWithRawAuth(string url, JObject payload, string authHeader) {
			Guard(url);
			using var req = new HttpRequestMessage(HttpMethod.Post, url) {
				Content = new StringContent(payload.ToString(Newtonsoft.Json.Formatting.None),
					Encoding.UTF8, "application/json"),
			};
			req.Headers.TryAddWithoutValidation("Authorization", authHeader);
			using var res = client.Send(req);
			return new RpcResponse(res.StatusCode, res.Content.ReadAsStringAsync().GetAwaiter().GetResult());
		}

		public static RpcResponse Get(string url, string? token = null, string? origin = null, string? host = null) =>
			Send(url, HttpMethod.Get, null, token, origin, host);

		public static RpcResponse Send(string url, HttpMethod method, string? body,
			string? token = null, string? origin = null, string? host = null) {
			Guard(url);
			using var req = new HttpRequestMessage(method, url);
			if (body is not null)
				req.Content = new StringContent(body, Encoding.UTF8, "application/json");
			Decorate(req, token, origin, host);
			using var res = client.Send(req);
			var text = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
			return new RpcResponse(res.StatusCode, text);
		}

		/// <summary>
		/// POSTs without a Content-Length so the request goes out chunked. The server cannot know the
		/// size up front, which is exactly the path that used to read an unbounded body.
		/// </summary>
		public static RpcResponse PostChunked(string url, int totalBytes, string? token = null) {
			Guard(url);
			using var req = new HttpRequestMessage(HttpMethod.Post, url);
			req.Content = new PushStreamContent(totalBytes);
			req.Headers.TransferEncodingChunked = true;
			Decorate(req, token, null, null);
			try {
				using var res = client.Send(req);
				var text = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
				return new RpcResponse(res.StatusCode, text);
			}
			catch (HttpRequestException) {
				// The server answers 413 and closes while we are still uploading; depending on timing the
				// socket dies before the status is read. Both outcomes mean "server refused to buffer it",
				// which is the behaviour under test, so report it as the same rejection.
				return new RpcResponse(HttpStatusCode.RequestEntityTooLarge, "<connection closed by server>");
			}
			catch (IOException) {
				return new RpcResponse(HttpStatusCode.RequestEntityTooLarge, "<connection closed by server>");
			}
		}

		static void Decorate(HttpRequestMessage req, string? token, string? origin, string? host) {
			if (token is not null)
				req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
			if (origin is not null)
				req.Headers.TryAddWithoutValidation("Origin", origin);
			if (host is not null)
				req.Headers.Host = host;
		}

		/// <summary>Streams filler bytes so the request body exceeds the server's cap.</summary>
		sealed class PushStreamContent : HttpContent {
			readonly int totalBytes;
			public PushStreamContent(int totalBytes) => this.totalBytes = totalBytes;

			protected override void SerializeToStream(Stream stream, System.Net.TransportContext? context,
				CancellationToken cancellationToken) {
				var chunk = new byte[64 * 1024];
				for (int i = 0; i < chunk.Length; i++)
					chunk[i] = (byte)'a';
				int written = 0;
				while (written < totalBytes) {
					var n = Math.Min(chunk.Length, totalBytes - written);
					stream.Write(chunk, 0, n);
					stream.Flush();
					written += n;
				}
			}

			protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) {
				SerializeToStream(stream, context, CancellationToken.None);
				return Task.CompletedTask;
			}

			protected override bool TryComputeLength(out long length) {
				length = -1;
				return false; // no Content-Length => chunked
			}
		}

		// ---- JSON-RPC payload builders ----

		public static JObject Request(int id, string method, JObject? @params = null) {
			var o = new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
			if (@params is not null)
				o["params"] = @params;
			return o;
		}

		public static JObject Notification(string method) =>
			new JObject { ["jsonrpc"] = "2.0", ["method"] = method };

		public static JObject CallTool(int id, string name, JObject? args = null) =>
			Request(id, "tools/call", new JObject {
				["name"] = name,
				["arguments"] = args ?? new JObject(),
			});
	}

	/// <summary>An open SSE stream whose frames can be awaited from a test.</summary>
	sealed class SseStream : IDisposable {
		readonly HttpClient client = new() { Timeout = Timeout.InfiniteTimeSpan };
		readonly List<string> frames = new();
		readonly object gate = new();
		readonly CancellationTokenSource cts = new();
		HttpResponseMessage? response;

		public HttpStatusCode Status { get; private set; }

		public SseStream(string url, string? token = null) {
			Rpc.Guard(url);
			var req = new HttpRequestMessage(HttpMethod.Get, url);
			req.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
			if (token is not null)
				req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
			response = client.Send(req, HttpCompletionOption.ResponseHeadersRead);
			Status = response.StatusCode;
			var stream = response.Content.ReadAsStream();
			new Thread(() => ReadLoop(stream)) { IsBackground = true }.Start();
		}

		void ReadLoop(Stream stream) {
			var buffer = new byte[4096];
			var sb = new StringBuilder();
			try {
				while (!cts.IsCancellationRequested) {
					var n = stream.Read(buffer, 0, buffer.Length);
					if (n <= 0)
						break;
					sb.Append(Encoding.UTF8.GetString(buffer, 0, n));
					// SSE frames are terminated by a blank line.
					string text;
					while ((text = sb.ToString()).Contains("\n\n")) {
						var idx = text.IndexOf("\n\n", StringComparison.Ordinal);
						var frame = text.Substring(0, idx);
						sb.Remove(0, idx + 2);
						lock (gate)
							frames.Add(frame);
					}
				}
			}
			catch (Exception) {
				// Stream torn down (server stopped or test finished) — nothing to do.
			}
		}

		/// <summary>Waits for a frame matching <paramref name="predicate"/>, or returns null on timeout.</summary>
		public string? WaitForFrame(Func<string, bool> predicate, int timeoutMs) {
			var deadline = Environment.TickCount64 + timeoutMs;
			while (Environment.TickCount64 < deadline) {
				lock (gate) {
					foreach (var f in frames) {
						if (predicate(f))
							return f;
					}
				}
				Thread.Sleep(25);
			}
			return null;
		}

		public void Dispose() {
			cts.Cancel();
			response?.Dispose();
			client.Dispose();
		}
	}
}
