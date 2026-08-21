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
using System.Globalization;
using System.Linq;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Contracts.Debugger.Evaluation;
using dnSpy.Contracts.Documents;
using dnSpy.MCP.Server;
using Newtonsoft.Json.Linq;
using static dnSpy.MCP.Tools.JsonUtils;

namespace dnSpy.MCP.Tools {
	/// <summary>
	/// Recovers obfuscated string constants. A string decryptor takes a constant (an int id or an
	/// already-encrypted string) and returns the plaintext at run time, so the plaintext is never a
	/// literal in the assembly. decrypt_strings finds the decryptor's call sites statically, reads the
	/// constant argument at each (the ldstr/ldc.i4 that precedes the call), and — when a session is
	/// paused — func-evals the decryptor once per distinct constant to get the plaintext back.
	///
	/// It needs both the static half (module + member resolution, reused from StaticTools) and the eval
	/// half (a frame in the paused process, reused from InspectionTools via <see cref="FrameEvaluator"/>).
	/// </summary>
	sealed class DecryptTools {
		readonly DbgAccess dbg;
		readonly Lazy<IDsDocumentService> documentService;
		readonly FrameEvaluator frameEval;

		public DecryptTools(DbgAccess dbg, Lazy<IDsDocumentService> documentService, Lazy<DbgLanguageService> languageService) {
			this.dbg = dbg;
			this.documentService = documentService;
			// dbg_eval uses only the DbgManager (via DbgAccess) and the language service; mirror that.
			frameEval = new FrameEvaluator(dbg, languageService);
		}

		public IEnumerable<ToolDef> Create() {
			yield return new ToolDef("decrypt_strings",
				"Recover obfuscated string constants produced by a static decryptor method. Finds every call site of the decryptor, reads the constant argument at each (an int id or a string, taken from the ldc.i4/ldstr that precedes the call), and — when a debug session is paused — func-evals the decryptor once per distinct constant to get the plaintext. With dry_run, or when nothing is paused, it returns the call sites and the distinct constants without decrypting. The decryptor must be static (v1). Identify it by fully-qualified name or metadata token.",
				Schema.Object(
					("module", Schema.Str("Module file path (of the decryptor), or file name if open in dnSpy"), true),
					("method", Schema.Str("Fully-qualified decryptor method, e.g. 'MyApp.Strings.Decrypt'"), false),
					("token", Schema.Str("Metadata token of the decryptor method, hex (0x06000001) or decimal"), false),
					("max_calls", Schema.Int("Maximum distinct constants to func-eval (default 100, clamped 1-2000)"), false),
					("dry_run", Schema.Bool("Only list call sites and distinct constants; do not evaluate (default false)"), false)),
				DecryptStrings,
				outputSchema: Schema.Object(
					("decryptor", Schema.Str("The resolved decryptor (dotted name)"), false),
					("decryptorToken", Schema.Str("The decryptor's metadata token"), false),
					("callSites", Schema.Arr("Call sites: method, methodToken, ilOffset, arg, argKind (int|string|non-constant)"), true),
					("results", Schema.Arr("Per distinct constant: arg, argKind, plaintext (or error)"), true),
					("evaluated", Schema.Int("How many distinct constants were func-evaled"), false),
					("skipped", Schema.Int("How many call sites had a non-constant argument"), false),
					("note", Schema.Str("Present when no plaintext was produced, explaining why"), false)));
		}

		string DecryptStrings(JObject args) {
			var module = ReqStr(args, "module");
			var method = (string?)args["method"];
			var token = (string?)args["token"];
			var maxCalls = Clamp((int?)args["max_calls"] ?? 100, 1, 2000);
			var dryRun = (bool?)args["dry_run"] ?? false;

			// Phase 1 — static: resolve the decryptor and scan its call sites for constant arguments.
			// Shares StaticTools' metadata lock because dnlib populates its tables lazily and that is not
			// safe from two threads on the same module at once.
			string decryptorFq, decryptorToken, declTypeName, simpleName;
			JArray callSites;
			List<Arg> distinct;
			int skipped;
			lock (StaticTools.MetadataLock) {
				var mod = MetadataResolver.ResolveModule(documentService.Value, module);
				var targets = StaticTools.ResolveTargets(mod, method, token);
				var instance = Array.Find(targets, t => !t.IsStatic);
				if (instance is not null)
					throw new InvalidOperationException(
						$"decryptor '{Dotted(instance)}' is not static; instance decryptors aren't supported yet");
				var primary = targets[0];
				declTypeName = CSharpTypeName(primary.DeclaringType);
				simpleName = primary.Name;
				decryptorFq = Dotted(primary);
				decryptorToken = Token(primary.MDToken);
				(callSites, distinct, skipped) = ScanCallSites(mod, targets);
			}

			// Phase 2 — decrypt, if we can. dry_run and "no paused session" both return the static call
			// sites with a note instead of erroring; only a genuine paused session produces plaintext.
			var results = new JArray();
			var evaluated = 0;
			string? note = null;
			if (dryRun)
				note = "dry_run: static call sites only; pause a debug session to func-eval and recover plaintext";
			else if (!IsPaused())
				note = "no paused debug session; returning static call sites only. Start a session (dbg_start) and pause it (hit a breakpoint) to func-eval and recover plaintext";
			else {
				var toEval = Math.Min(distinct.Count, maxCalls);
				try {
					// One frame context for the whole batch: each distinct constant becomes an expression
					// '<DeclaringType>.<Method>(<literal>)' evaluated the same way dbg_eval does.
					frameEval.WithFrame(null, 0, (evalInfo, language) => {
						for (int i = 0; i < toEval; i++) {
							var a = distinct[i];
							var expr = declTypeName + "." + simpleName + "(" + a.Literal + ")";
							var r = new JObject { ["arg"] = a.ArgToken(), ["argKind"] = a.Kind, ["expression"] = expr };
							try {
								var (value, thrown) = frameEval.Evaluate(evalInfo, language, expr);
								r["plaintext"] = value;
								if (thrown)
									r["isThrownException"] = true;
							}
							catch (Exception ex) {
								r["error"] = ex.Message;
							}
							results.Add(r);
						}
						return "";
					});
				}
				catch (Exception ex) {
					// The process reported paused but no frame could answer (a runtime thread with no
					// managed stack, say). The static call sites are still useful, so note it, don't fail.
					note = "paused, but no evaluation frame was available: " + ex.Message;
				}
				evaluated = results.Count;
				if (note is null && distinct.Count > maxCalls)
					note = $"evaluated the first {maxCalls} of {distinct.Count} distinct constants; raise max_calls to decrypt the rest";
			}

			var outObj = new JObject {
				["decryptor"] = decryptorFq,
				["decryptorToken"] = decryptorToken,
				["callSites"] = callSites,
				["results"] = results,
				["evaluated"] = evaluated,
				["skipped"] = skipped,
			};
			if (note is not null)
				outObj["note"] = note;
			return Json(outObj);
		}

		bool IsPaused() => dbg.Invoke(() => dbg.DbgManager.IsDebugging && dbg.DbgManager.IsRunning == false);

		// Scan every method body in the module for calls to any of the decryptor overloads. At each call,
		// read the immediately-preceding real instruction (Debug builds insert statement-boundary nops) for
		// a constant argument. Returns the call-site list, the DISTINCT constants to evaluate, and how many
		// call sites had a non-constant argument.
		(JArray callSites, List<Arg> distinct, int skipped) ScanCallSites(ModuleDef mod, MethodDef[] targets) {
			var targetKeys = new HashSet<string>(targets.Select(MethodKey));
			var targetNames = new HashSet<string>(targets.Select(t => (string)t.Name)); // prefilter before the resolve
			var callSites = new JArray();
			var distinct = new List<Arg>();
			var seen = new HashSet<string>();
			var skipped = 0;

			foreach (var caller in EnumerateMethods(mod)) {
				if (!caller.HasBody)
					continue;
				var instrs = caller.Body.Instructions;
				for (int i = 0; i < instrs.Count; i++) {
					var instr = instrs[i];
					if (instr.OpCode.Code is not (Code.Call or Code.Callvirt))
						continue;
					if (instr.Operand is not IMethod called || !targetNames.Contains((string)called.Name))
						continue;
					if (called.ResolveMethodDef() is not MethodDef md || !targetKeys.Contains(MethodKey(md)))
						continue;

					var arg = ReadConstant(PrecedingReal(instrs, i));
					callSites.Add(new JObject {
						["method"] = Dotted(caller),
						["methodToken"] = Token(caller.MDToken),
						["ilOffset"] = "0x" + instr.Offset.ToString("X"),
						["arg"] = arg.ArgToken(),
						["argKind"] = arg.Kind,
					});
					if (!arg.IsConstant)
						skipped++;
					else if (seen.Add(arg.Key))
						distinct.Add(arg);
				}
			}
			return (callSites, distinct, skipped);
		}

		// The immediately-preceding real instruction, skipping nops so a Debug build's statement-boundary
		// nops don't mask an otherwise-constant argument. null if the call is the first real instruction.
		static Instruction? PrecedingReal(IList<Instruction> instrs, int callIndex) {
			for (int j = callIndex - 1; j >= 0; j--)
				if (instrs[j].OpCode.Code != Code.Nop)
					return instrs[j];
			return null;
		}

		// Classify a preceding instruction as a constant argument: ldstr => string, ldc.i4* => int,
		// anything else (or none) => non-constant.
		static Arg ReadConstant(Instruction? prev) {
			if (prev is not null) {
				if (prev.OpCode.Code == Code.Ldstr && prev.Operand is string s)
					return Arg.String(s);
				if (TryLdcI4(prev, out var n))
					return Arg.Int(n);
			}
			return Arg.NonConstant();
		}

		// Decode every ldc.i4 form (full, short, and the 0..8 / m1 macros) to its int value.
		static bool TryLdcI4(Instruction instr, out int value) {
			switch (instr.OpCode.Code) {
			case Code.Ldc_I4: value = (int)instr.Operand; return true;
			case Code.Ldc_I4_S: value = (sbyte)instr.Operand; return true;
			case Code.Ldc_I4_M1: value = -1; return true;
			case Code.Ldc_I4_0: value = 0; return true;
			case Code.Ldc_I4_1: value = 1; return true;
			case Code.Ldc_I4_2: value = 2; return true;
			case Code.Ldc_I4_3: value = 3; return true;
			case Code.Ldc_I4_4: value = 4; return true;
			case Code.Ldc_I4_5: value = 5; return true;
			case Code.Ldc_I4_6: value = 6; return true;
			case Code.Ldc_I4_7: value = 7; return true;
			case Code.Ldc_I4_8: value = 8; return true;
			default: value = 0; return false;
			}
		}

		/// <summary>A constant call-site argument: an int id, an encrypted string, or a non-constant marker.</summary>
		readonly struct Arg {
			public string Kind { get; }             // "int" | "string" | "non-constant"
			readonly int intValue;
			readonly string? stringValue;
			public string? Literal { get; }         // the C# literal for the func-eval expression
			Arg(string kind, int intValue, string? stringValue, string? literal) {
				Kind = kind;
				this.intValue = intValue;
				this.stringValue = stringValue;
				Literal = literal;
			}
			public static Arg Int(int v) => new("int", v, null, v.ToString(CultureInfo.InvariantCulture));
			public static Arg String(string v) => new("string", 0, v, "\"" + Escape(v) + "\"");
			public static Arg NonConstant() => new("non-constant", 0, null, null);
			public bool IsConstant => Kind != "non-constant";
			// A distinct key that keeps int 1 and string "1" apart.
			public string Key => Kind == "int" ? "int\0" + intValue.ToString(CultureInfo.InvariantCulture) : "string\0" + stringValue;
			// A fresh JValue each call — a JToken may not be parented into two containers.
			public JToken ArgToken() => Kind switch {
				"int" => new JValue(intValue),
				"string" => new JValue(stringValue),
				_ => JValue.CreateNull(),
			};
		}

		// A C# string literal body (without the surrounding quotes), escaping what would break the eval.
		static string Escape(string s) {
			var sb = new StringBuilder(s.Length + 2);
			foreach (var c in s) {
				switch (c) {
				case '\\': sb.Append("\\\\"); break;
				case '"': sb.Append("\\\""); break;
				case '\n': sb.Append("\\n"); break;
				case '\r': sb.Append("\\r"); break;
				case '\t': sb.Append("\\t"); break;
				case '\0': sb.Append("\\0"); break;
				default: sb.Append(c); break;
				}
			}
			return sb.ToString();
		}

		// The declaring type as a C# name: dnlib's FullName uses '/' (and reflection names '+') between a
		// nested type and its encloser, which C# spells with '.'.
		static string CSharpTypeName(TypeDef? t) => (t?.FullName ?? "").Replace('/', '.').Replace('+', '.');

		static IEnumerable<MethodDef> EnumerateMethods(ModuleDef mod) {
			foreach (var t in mod.GetTypes())
				foreach (var m in t.Methods)
					yield return m;
		}

		// A method's cross-module identity — token plus defining module — so a MemberRef from elsewhere
		// still lines up with the decryptor MethodDef. Matches StaticTools' MethodKey.
		static string MethodKey(MethodDef m) =>
			m.MDToken.Raw.ToString("X8") + "@" + (m.Module?.Location?.ToLowerInvariant() ?? "");

		static string Dotted(IMemberDef m) => (m.DeclaringType?.FullName ?? "") + "." + m.Name;

		static string Token(MDToken token) => "0x" + token.Raw.ToString("X8");

		static string ReqStr(JObject args, string name) =>
			(string?)args[name] ?? throw new ArgumentException($"'{name}' is required");
	}
}
