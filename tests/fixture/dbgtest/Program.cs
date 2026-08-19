using System;
using System.Threading;

// Fixture debuggee for the Tier 2 integration tests. Everything here is deliberately deterministic:
// the tests assert on exact local values, exact method tokens and exact source lines, so nothing may
// depend on timing, the environment or randomness.
namespace DbgTest {
	public static class Program {
		// Asserted by the statics tests.
		public static int Counter;
		public static readonly int[] Numbers = { 10, 20, 30, 40, 50 };

		public static void Main() {
			Console.WriteLine("dbgtest ready");

			// A steady loop the tests can break into repeatedly: hit counts and conditions need a
			// breakpoint that is reached many times with a changing argument.
			for (int i = 1; i <= 1000; i++) {
				Counter = i;
				var sum = Add(i, 1);
				var wide = Add(i, 1, 2);
				if (i % 100 == 0)
					Console.WriteLine($"iteration {i} sum {sum} wide {wide}");
				Thread.Sleep(50);
			}

			Outer.Inner.Ping();
			Boom();
			Console.WriteLine("dbgtest done");
		}

		/// <summary>Two-argument overload. Locals a/b are read by the locals and eval tests.</summary>
		public static int Add(int a, int b) {
			var result = a + b;
			return result;
		}

		/// <summary>Second overload, so "set a breakpoint on every overload" has something to find.</summary>
		public static int Add(int a, int b, int c) => a + b + c;

		/// <summary>Throws and catches, for the break-on-thrown-exception tool.</summary>
		public static void Boom() {
			try {
				throw new InvalidOperationException("fixture exception");
			}
			catch (InvalidOperationException) {
				Console.WriteLine("caught");
			}
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
