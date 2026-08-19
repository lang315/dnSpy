using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

// Fixture debuggee for the Tier 2 integration tests. Everything here is deliberately deterministic:
// the tests assert on exact local values, exact method tokens and exact source lines, so nothing may
// depend on timing, the environment or randomness.
namespace DbgTest {
	public static class Program {
		// Asserted by the statics tests.
		public static int Counter;
		public static readonly int[] Numbers = { 10, 20, 30, 40, 50 };

		/// <summary>Writable target for the memory-write round trip.</summary>
		public static readonly byte[] Scratch = new byte[16];

		/// <summary>
		/// Address of <see cref="Scratch"/>'s first element, published so a test can write to real
		/// memory. The debuggee pins it itself because the obvious alternative — asking the debugger to
		/// func-eval GCHandle.Alloc or Marshal.AllocHGlobal — does not work: dnSpy's evaluator answers
		/// ArgumentOutOfRangeException for both, while ordinary calls like Math.Max(3, 4) evaluate fine.
		/// </summary>
		public static long ScratchAddress;

		public static void Main() {
			Console.WriteLine("dbgtest ready");
			// Never freed: the pin has to outlive every test that writes through the address, and the
			// fixture is killed at the end of the run.
			var pin = GCHandle.Alloc(Scratch, GCHandleType.Pinned);
			ScratchAddress = pin.AddrOfPinnedObject().ToInt64();
			StartWorker();

			// A steady loop the tests can break into repeatedly: hit counts and conditions need a
			// breakpoint that is reached many times with a changing argument.
			for (int i = 1; i <= 1000; i++) {
				Counter = i;
				var sum = Add(i, 1);
				var wide = Add(i, 1, 2);
				Level1(i);
				Inspect(i);
				// First-chance exception on every pass, so break-on-exception does not have to wait
				// for the loop to finish the way a once-at-the-end throw would.
				Boom();
				if (i % 100 == 0)
					Console.WriteLine($"iteration {i} sum {sum} wide {wide}");
				Thread.Sleep(50);
			}

			Outer.Inner.Ping();
			Console.WriteLine("dbgtest done");
		}

		/// <summary>Two-argument overload. Locals a/b are read by the locals and eval tests.</summary>
		public static int Add(int a, int b) {
			var result = a + b;
			return result;
		}

		/// <summary>Second overload, so "set a breakpoint on every overload" has something to find.</summary>
		public static int Add(int a, int b, int c) => a + b + c;

		// A three-deep call chain. Stepping and frame_index need a stack with distinct, checkable
		// locals at every level. Chosen so seed 7 gives Level3(16) -> 116, with two==8 and mid==16 in
		// the caller and seed==7, one==8 above that: every assertion is a constant you can verify by
		// hand rather than a value the test discovers and then trusts.
		public static int Level1(int seed) {
			var one = seed + 1;
			return Level2(one);
		}

		public static int Level2(int two) {
			var mid = two * 2;
			return Level3(mid);
		}

		public static int Level3(int three) {
			var deep = three + 100;
			return deep;
		}

		/// <summary>A frame holding non-numeric locals: value formatting is only tested on ints elsewhere.</summary>
		public static string Inspect(int seed) {
			var text = "hello";
			var letters = new[] { "a", "b", "c" };
			var list = new List<int> { 7, 8, 9 };
			var graph = BuildGraph();
			var total = seed + letters.Length + list.Count;
			return text + total + graph.Name;
		}

		/// <summary>Three levels deep, so dbg_expand has something to walk into more than once.</summary>
		public static Node BuildGraph() =>
			new Node {
				Name = "root", Value = 1,
				Child = new Node {
					Name = "child", Value = 2,
					Child = new Node { Name = "leaf", Value = 3 },
				},
			};

		/// <summary>Throws and catches, for the break-on-thrown-exception tool.</summary>
		public static void Boom() {
			try {
				throw new InvalidOperationException("fixture exception");
			}
			catch (InvalidOperationException) {
			}
		}

		// A second thread so thread_id and dbg_set_thread have something to distinguish. It must never
		// touch Add or the Level chain: the existing tests break there and assume the main thread, and
		// a second caller would let them stop on the wrong one and fail at random.
		static void StartWorker() {
			var worker = new Thread(WorkerLoop) { IsBackground = true, Name = WorkerThreadName };
			worker.Start();
		}

		public const string WorkerThreadName = "dbgtest-worker";

		static void WorkerLoop() {
			while (true) {
				WorkerTick();
				Thread.Sleep(100);
			}
		}

		/// <summary>Only the worker thread ever calls this, so a breakpoint here pins that thread.</summary>
		public static int WorkerTick() {
			var ticks = 99;
			return ticks;
		}
	}

	/// <summary>Nested type, reached as "DbgTest.Outer.Inner.Ping" — the '.' to '+' resolution path.</summary>
	public static class Outer {
		public static class Inner {
			public static string Ping() => "pong";
		}
	}

	/// <summary>An object graph for the expand tool to walk into.</summary>
	public sealed class Node {
		public string Name { get; set; } = "root";
		public int Value { get; set; } = 42;
		public Node? Child { get; set; }
	}

	/// <summary>
	/// Reading this property blocks for longer than the old 10s dispatcher timeout, which is how the
	/// eval-timeout regression is exercised.
	/// </summary>
	public static class Slow {
		public static int SlowProperty {
			get {
				Thread.Sleep(15000);
				return 123;
			}
		}
	}
}
