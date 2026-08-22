using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace dnSpy.MCP.IntegrationTests {
	/// <summary>
	/// Walks the paused debuggee's managed GC heap with ClrMD (heap_stats / heap_find / heap_object) and
	/// asserts against the fixture's rooted Widget scene. This is also the coexistence gate: the tools
	/// attach ClrMD *passive* to the very process CorDebug is already debugging, so a call that hangs or
	/// crashes here (rather than returning data) is the signal that the second passive DAC fights the
	/// active ICorDebug attach. Every call has a bounded wait, so a coexistence failure surfaces as a
	/// timeout/assertion, never an indefinite hang.
	/// </summary>
	[Collection("dnSpy")]
	public class HeapIntegrationTests : IDisposable {
		public HeapIntegrationTests() {
			Dbg.Reset();
			PauseInsideAdd();
		}

		public void Dispose() => Dbg.Reset();

		/// <summary>
		/// Stops the fixture inside Program.Add (a == 7). Add is called from the main loop, which runs
		/// only after Warmup has populated Program.Widgets — so all five Widgets are alive and rooted on
		/// the heap at this breakpoint, and the process is frozen so their addresses stay stable across
		/// the separate ClrMD attaches heap_find and heap_object each make.
		/// </summary>
		static void PauseInsideAdd() {
			Dbg.Call("bp_add", new JObject {
				["module"] = Dbg.FixtureDll(),
				["token"] = Dbg.TokenOf("DbgTest.Program.Add"),
				["condition"] = "a == 7",
			});
			Dbg.Call("dbg_start", new JObject { ["path"] = Dbg.FixtureDll() });
			Dbg.WaitForBreak();
		}

		// Red before impl: heap_stats is not a registered tool yet (Unknown tool -> protocol error), and
		// the fixture has no rooted Widget scene, so neither the histogram nor the type line can exist.
		[DbgFact]
		public void Heap_stats_histograms_the_live_heap_and_lists_the_rooted_widget_type() {
			// top is set high so DbgTest.Widget (only five small objects) is not pushed out of the
			// top-N-by-bytes cut by the hundreds of BCL types on any real .NET heap.
			var res = Dbg.CallJson("heap_stats", new JObject { ["top"] = 100000 });

			Assert.True((long?)res["totalObjects"] > 0, "a live .NET heap must contain objects");
			Assert.True((long?)res["totalBytes"] > 0, "a live .NET heap must occupy bytes");

			var types = (JArray)res["types"]!;
			var widget = types.FirstOrDefault(t => (string?)t["type"] == "DbgTest.Widget");
			Assert.NotNull(widget);
			Assert.True((long?)widget!["count"] >= 5, "the fixture roots five Widgets");
			Assert.True((long?)widget["totalBytes"] > 0, "the Widget line must report a byte total");
		}

		// Red before impl: heap_find does not exist, and without the fixture scene there is nothing named
		// DbgTest.Widget to return with a "widget-N" Name field.
		[DbgFact]
		public void Heap_find_returns_every_rooted_widget_with_readable_fields() {
			var res = Dbg.CallJson("heap_find", new JObject { ["type"] = "DbgTest.Widget" });

			Assert.Equal("DbgTest.Widget", (string?)res["type"]);
			Assert.True((long?)res["count"] >= 5, "the fixture roots five Widgets");

			var instances = (JArray)res["instances"]!;
			Assert.True(instances.Count >= 5, "all five Widgets should be detailed (well under the default cap)");

			foreach (var inst in instances) {
				var addr = (string?)inst["address"];
				Assert.StartsWith("0x", addr, StringComparison.Ordinal);
				Assert.True((long?)inst["size"] > 0, "each Widget must report a non-zero size");
			}

			// The five Name fields recovered from the heap must be exactly the values Warmup assigned.
			var names = instances
				.Select(i => (string?)i["fields"]?["Name"])
				.Where(n => n is not null)
				.ToHashSet(StringComparer.Ordinal);
			for (int id = 0; id < 5; id++)
				Assert.Contains($"widget-{id}", names);
		}

		// Red before impl: heap_object does not exist. Depends on heap_find handing back a usable address.
		[DbgFact]
		public void Heap_object_reads_a_single_widget_by_the_address_heap_find_returned() {
			var found = Dbg.CallJson("heap_find", new JObject { ["type"] = "DbgTest.Widget", ["max"] = 5 });
			var instances = (JArray)found["instances"]!;
			Assert.NotEmpty(instances);
			var address = (string?)instances[0]["address"]
				?? throw new InvalidOperationException("heap_find returned an instance with no address");

			var obj = Dbg.CallJson("heap_object", new JObject { ["address"] = address });

			Assert.Equal("DbgTest.Widget", (string?)obj["type"]);
			Assert.False((bool?)obj["isArray"], "a Widget is not an array");
			Assert.True((long?)obj["size"] > 0, "the object must report a non-zero size");

			var name = (string?)obj["fields"]?["Name"];
			Assert.StartsWith("widget-", name, StringComparison.Ordinal);
		}

		// Red before impl: with no tool registered this is an Unknown-tool protocol error, not the clean
		// "must be paused" tool error the contract promises. After impl: no session at all must still be
		// reported as a tool error, never a raw transport fault or a hang.
		[DbgFact]
		public void A_heap_walk_with_no_paused_session_is_reported_cleanly() {
			Dbg.Reset(); // drop the session the constructor started — nothing is loaded or paused now

			var error = Dbg.CallExpectingError("heap_stats", new JObject());

			Assert.False(string.IsNullOrWhiteSpace(error));
			Assert.Contains("paus", error, StringComparison.OrdinalIgnoreCase);
		}

		// Red before impl: heap_find does not exist. After impl: a type that matches nothing is a normal
		// empty answer (count 0), not an error — CallJson throws on a tool error, so a clean return here
		// is itself the assertion that it was not reported as one.
		[DbgFact]
		public void Heap_find_on_an_unknown_type_returns_zero_instances_not_an_error() {
			var res = Dbg.CallJson("heap_find", new JObject { ["type"] = "DbgTest.NoSuchTypeExistsHere" });

			Assert.Equal(0, (int?)res["count"]);
			Assert.Empty((JArray)res["instances"]!);
			Assert.False((bool?)res["truncated"], "nothing was skipped, so nothing was truncated");
		}
	}
}
