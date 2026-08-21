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
		// Written by SetState, read by ReadState — a read/write pair in distinct methods for field find_references.
		public static int State;
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
			// Touch the analysis-only members (interface impls, a virtual override, IOC strings, a P/Invoke)
			// once so the compiler emits them all; find_implementations and extract_iocs read them statically.
			Warmup();
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

		/// <summary>
		/// Called three times from <see cref="TickLoop"/>. The tracepoint tests set a tracepoint here: it
		/// logs on every hit and auto-resumes, so they see repeated logging without a pause. The parameter
		/// <paramref name="i"/> is in scope at method entry, so a "{i}" trace message interpolates 0,1,2.
		/// </summary>
		public static int Tick(int i) => i * 2;

		/// <summary>Calls <see cref="Tick"/> in a short loop — the tracepoint multiplicity fixture. Reached from Warmup.</summary>
		static void TickLoop() {
			for (int i = 0; i < 3; i++)
				GC.KeepAlive(Tick(i));
		}

		[DllImport("kernel32.dll", EntryPoint = "GetTickCount")]
		static extern uint NativeGetTickCount();

		/// <summary>
		/// Synthetic indicators of compromise for the extract_iocs test. None of these are real
		/// infrastructure — they exist only as string literals for the static extractor to find.
		/// </summary>
		public static string Indicators() {
			var url = "http://example.com/beacon";
			var ip = "192.168.10.50";
			var registry = "HKLM\\SOFTWARE\\DbgTest\\Config";
			var path = "C:\\Windows\\Temp\\payload.bin";
			var email = "operator@dbgtest.invalid";
			return url + " " + ip + " " + registry + " " + path + " " + email;
		}

		/// <summary>Writes the State field — the write site for field find_references.</summary>
		public static void SetState(int v) => State = v;

		/// <summary>Reads the State field — the read site for field find_references.</summary>
		public static int ReadState() => State * 2;

		/// <summary>Reads the embedded resource — a read site and the target for extract_resource.</summary>
		public static string ReadEmbedded() {
			using var s = typeof(Program).Assembly.GetManifestResourceStream("dbgtest.embedded.txt");
			using var r = new System.IO.StreamReader(s!);
			return r.ReadToEnd();
		}

		// --- String-obfuscation fixture for the decrypt_strings tool ---
		// Dec is a deliberately trivial, deterministic string "decryptor": each id selects a blob that
		// is the UTF-8 plaintext XORed byte-for-byte with DecKey. The plaintext ("secret-one",
		// "secret-two") therefore never appears as a literal in the assembly — the only way to recover
		// it is to run Dec, which is exactly what decrypt_strings does by func-eval. The static scan sees
		// only the constant ids at the call sites (Program.UseSecrets); the plaintext comes from the run.
		const byte DecKey = 0x5A;

		// XOR(plaintext, 0x5A), stored as literals so nothing here reveals the plaintext statically.
		static readonly byte[][] EncTable = {
			/* id 0: unused */ Array.Empty<byte>(),
			/* id 1 */ new byte[] { 0x29, 0x3F, 0x39, 0x28, 0x3F, 0x2E, 0x77, 0x35, 0x34, 0x3F },
			/* id 2 */ new byte[] { 0x29, 0x3F, 0x39, 0x28, 0x3F, 0x2E, 0x77, 0x2E, 0x2D, 0x35 },
		};

		/// <summary>
		/// Static string decryptor — the target of decrypt_strings. Dec(1) == "secret-one",
		/// Dec(2) == "secret-two". Single-byte XOR, so it is trivially reversible and deterministic.
		/// </summary>
		public static string Dec(int id) {
			var enc = EncTable[id];
			var buf = new byte[enc.Length];
			for (int i = 0; i < enc.Length; i++)
				buf[i] = (byte)(enc[i] ^ DecKey);
			return System.Text.Encoding.UTF8.GetString(buf);
		}

		/// <summary>
		/// Call sites for decrypt_strings: two calls to Dec with distinct constant ids that the static
		/// scan can read (ldc.i4.1/ldc.i4.2 then call). Reached from Warmup so the methods are emitted
		/// and a call graph can see them; GC.KeepAlive stops the results being elided.
		/// </summary>
		public static void UseSecrets() {
			var s1 = Dec(1);
			var s2 = Dec(2);
			GC.KeepAlive(s1 + s2);
		}

		/// <summary>
		/// Exercises the interface implementations, the virtual override, the P/Invoke, the State field and
		/// the embedded resource so the compiler emits them all; the static tools then read them off disk.
		/// </summary>
		static void Warmup() {
			IGreeter[] greeters = { new EnglishGreeter(), new FrenchGreeter() };
			Animal animal = new Dog();
			SetState(7);
			UseSecrets();
			TickLoop();
			GC.KeepAlive(Indicators() + greeters[0].Greet() + greeters[1].Greet() + animal.Speak()
				+ NativeGetTickCount() + ReadState() + ReadEmbedded());
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

	/// <summary>An interface implemented by two concrete types — the target for find_implementations.</summary>
	public interface IGreeter {
		string Greet();
	}

	public sealed class EnglishGreeter : IGreeter {
		public string Greet() => "hello-en";
	}

	public sealed class FrenchGreeter : IGreeter {
		public string Greet() => "bonjour";
	}

	/// <summary>A virtual method overridden by exactly one derived type — the other find_implementations case.</summary>
	public class Animal {
		public virtual string Speak() => "...";
	}

	public sealed class Dog : Animal {
		public override string Speak() => "woof";
	}
}
