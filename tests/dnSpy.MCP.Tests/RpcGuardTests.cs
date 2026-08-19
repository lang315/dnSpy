using System;
using Xunit;

namespace dnSpy.MCP.Tests {
	/// <summary>
	/// The integration suite arms Rpc.RequestGuard with its settings-file interlock so that even a test
	/// which talks to Rpc or SseStream directly cannot reach a non-isolated dnSpy. These tests pin the
	/// hook down: every path that opens a socket must run the guard first, and it must run before any
	/// connection is attempted. If the guard stopped covering a path, the interlock would have a hole.
	/// </summary>
	// The assembly disables test parallelization (see AssemblyInfo), so mutating the static
	// Rpc.RequestGuard here cannot race another test.
	public class RpcGuardTests : IDisposable {
		public void Dispose() => Rpc.RequestGuard = null;

		sealed class Tripwire : Exception {
			public Tripwire() : base("guard fired") { }
		}

		// A URL that is never listened on, so if the guard does NOT fire the call fails with a transport
		// error instead — which the test tells apart from the guard firing.
		const string DeadUrl = "http://127.0.0.1:9/mcp";

		[Fact]
		public void Post_runs_the_guard_before_touching_the_network() {
			Rpc.RequestGuard = _ => throw new Tripwire();
			Assert.Throws<Tripwire>(() => Rpc.Post(DeadUrl, Rpc.Request(1, "ping")));
		}

		[Fact]
		public void The_guard_receives_the_target_url() {
			string? seen = null;
			Rpc.RequestGuard = url => { seen = url; throw new Tripwire(); };
			Assert.Throws<Tripwire>(() => Rpc.Post(DeadUrl, Rpc.Request(1, "ping")));
			Assert.Equal(DeadUrl, seen);
		}

		[Fact]
		public void Get_runs_the_guard() {
			Rpc.RequestGuard = _ => throw new Tripwire();
			Assert.Throws<Tripwire>(() => Rpc.Get(DeadUrl));
		}

		[Fact]
		public void The_chunked_upload_path_runs_the_guard() {
			Rpc.RequestGuard = _ => throw new Tripwire();
			Assert.Throws<Tripwire>(() => Rpc.PostChunked(DeadUrl, 16));
		}

		[Fact]
		public void The_raw_auth_path_runs_the_guard() {
			Rpc.RequestGuard = _ => throw new Tripwire();
			Assert.Throws<Tripwire>(() => Rpc.PostWithRawAuth(DeadUrl, Rpc.Request(1, "ping"), "Bearer x"));
		}

		// SseStream is the notification tests' door to dnSpy; it must be guarded like everything else.
		[Fact]
		public void Opening_an_sse_stream_runs_the_guard() {
			Rpc.RequestGuard = _ => throw new Tripwire();
			Assert.Throws<Tripwire>(() => new SseStream(DeadUrl));
		}

		[Fact]
		public void With_no_guard_set_requests_are_not_gated() {
			Rpc.RequestGuard = null;
			// No guard, so this reaches the network and fails as a transport error, not a Tripwire.
			Assert.ThrowsAny<Exception>(() => Rpc.Post(DeadUrl, Rpc.Request(1, "ping")));
		}
	}
}
