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
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents;
using Newtonsoft.Json.Linq;
using static dnSpy.MCP.Tools.JsonUtils;

namespace dnSpy.MCP.Tools {
	/// <summary>
	/// Static analysis of assemblies — on disk or already open in dnSpy — with no debug session:
	/// list and search types and methods, and decompile to C#. This is what lets an agent read and
	/// explore an unknown assembly instead of only driving the debugger through names it already knows.
	/// </summary>
	sealed class StaticTools {
		const int MaxDecompileChars = 200_000;
		const int MaxListItems = 5000;
		const int DecompileTimeoutMs = 60_000;

		// dnlib populates its metadata tables lazily on first access, which is not safe to do from two
		// threads at once. Static-tool calls are not latency-critical and come from one agent, so
		// serialising them is a cheap way to stay correct.
		static readonly object metadataLock = new();

		readonly Lazy<IDsDocumentService> documentService;
		readonly Lazy<IDecompilerService> decompilerService;

		public StaticTools(Lazy<IDsDocumentService> documentService, Lazy<IDecompilerService> decompilerService) {
			this.documentService = documentService;
			this.decompilerService = decompilerService;
		}

		public IEnumerable<ToolDef> Create() {
			yield return new ToolDef("decompile",
				"Decompile a method or type to C# (or IL). Identify it by fully-qualified name (all overloads, for a method) or by metadata token. Works on any assembly given a full path or already open in dnSpy; no debug session required.",
				Schema.Object(
					("module", Schema.Str("Module file path, or file name if already open in dnSpy"), true),
					("method", Schema.Str("Fully-qualified method, e.g. 'MyApp.Program.Main' (decompiles every overload)"), false),
					("type", Schema.Str("Fully-qualified type, e.g. 'MyApp.Program' (decompiles the whole type)"), false),
					("token", Schema.Str("Metadata token of a method or type, hex (0x06000001) or decimal"), false),
					("format", Schema.Str("'csharp' (default) or 'il' — IL shows exact opcodes and offsets, useful for obfuscated code"), false)),
				Decompile, readOnly: true);

			yield return new ToolDef("list_types",
				"List the types in a module, optionally filtered by a name pattern (wildcards * and ?). Returns each type's full name, metadata token and kind. Use it to discover what an assembly contains.",
				Schema.Object(
					("module", Schema.Str("Module file path, or file name if already open in dnSpy"), true),
					("filter", Schema.Str("Name pattern, case-insensitive, wildcards * and ? (matched against the full name)"), false),
					("max", Schema.Int("Maximum types to return (default 500)"), false)),
				ListTypes, readOnly: true);

			yield return new ToolDef("list_methods",
				"List the methods of a type with their metadata tokens and signatures. Feed the tokens to bp_add or decompile.",
				Schema.Object(
					("module", Schema.Str("Module file path, or file name if already open in dnSpy"), true),
					("type", Schema.Str("Fully-qualified type, e.g. 'MyApp.Program'"), true)),
				ListMethods, readOnly: true);

			yield return new ToolDef("search",
				"Search a module for member names (wildcards * and ?) and/or string literals used in method bodies. Great as the first step on an unknown assembly — find where a URL, error message or key appears, or which members match a pattern.",
				Schema.Object(
					("module", Schema.Str("Module file path, or file name if already open in dnSpy"), true),
					("query", Schema.Str("Name pattern (wildcards * ?) for names; substring for string literals"), true),
					("kind", Schema.Str("names | strings | all (default all)"), false),
					("max", Schema.Int("Maximum hits to return (default 200)"), false)),
				Search, readOnly: true);

			yield return new ToolDef("find_references",
				"Find where a method, field or type is used, without running the program. Method → its callers; field → its reads/writes; type → the methods that use it (instantiate, cast, call, catch, local). Identify the target by fully-qualified name (method/field/type) or by metadata token (kind auto-detected).",
				Schema.Object(
					("module", Schema.Str("Module file path (of the target), or file name if open in dnSpy"), true),
					("method", Schema.Str("Fully-qualified target method, e.g. 'MyApp.Program.Add'"), false),
					("field", Schema.Str("Fully-qualified target field, e.g. 'MyApp.Program.Counter'"), false),
					("type", Schema.Str("Fully-qualified target type, e.g. 'MyApp.Node'"), false),
					("token", Schema.Str("Metadata token of a method, field or type (hex or decimal); kind auto-detected"), false),
					("access", Schema.Str("For a field target: reads | writes | all (default all)"), false),
					("scope", Schema.Str("'module' (default, the target's module) or 'open' (every assembly open in dnSpy)"), false),
					("max", Schema.Int("Maximum results to return (default 200)"), false)),
				FindReferences, readOnly: true);

			yield return new ToolDef("find_implementations",
				"Find the methods that override or implement a given virtual, abstract or interface method — the forward direction of the type hierarchy, complementing find_references. Identify the target by fully-qualified name (all overloads) or metadata token.",
				Schema.Object(
					("module", Schema.Str("Module file path (of the target), or file name if open in dnSpy"), true),
					("method", Schema.Str("Fully-qualified target method, e.g. 'MyApp.IHandler.Handle'"), false),
					("token", Schema.Str("Metadata token of the target method, hex or decimal"), false),
					("scope", Schema.Str("'module' (default, the target's module) or 'open' (every assembly open in dnSpy)"), false),
					("max", Schema.Int("Maximum implementations to return (default 200)"), false)),
				FindImplementations, readOnly: true);

			yield return new ToolDef("type_hierarchy",
				"Show a type's base types (up to System.Object) and the interfaces it implements, and/or its derived types (subclasses and interface implementers) within the module or every open assembly.",
				Schema.Object(
					("module", Schema.Str("Module file path, or file name if open in dnSpy"), true),
					("type", Schema.Str("Fully-qualified type, e.g. 'MyApp.Animal'"), false),
					("token", Schema.Str("Metadata token of the type, hex or decimal"), false),
					("direction", Schema.Str("base | derived | both (default both)"), false),
					("scope", Schema.Str("'module' (default) or 'open' — for the derived-types search"), false),
					("max", Schema.Int("Maximum derived types to return (default 500)"), false)),
				TypeHierarchy, readOnly: true);

			yield return new ToolDef("extract_iocs",
				"Extract indicators of compromise from an assembly by pure static reading: URLs, IPs, registry keys, file paths and e-mail addresses in string literals, plus native imports (P/Invoke), each tied to the method it appears in. For triage and malware-analysis reporting — nothing is executed.",
				Schema.Object(
					("module", Schema.Str("Module file path, or file name if already open in dnSpy"), true),
					("categories", Schema.Str("Comma-separated subset of: url, ip, registry, path, email, pinvoke, base64 (default: all but base64)"), false),
					("max", Schema.Int("Maximum indicators to return (default 500)"), false)),
				ExtractIocs, readOnly: true);

			yield return new ToolDef("list_resources",
				"List a module's manifest resources — name, type, visibility, and byte length for embedded ones. Packers and obfuscators often hide payloads or config in resources.",
				Schema.Object(
					("module", Schema.Str("Module file path, or file name if open in dnSpy"), true)),
				ListResources, readOnly: true);

			yield return new ToolDef("extract_resource",
				"Extract an embedded resource's bytes by name. With 'save_path' it writes the bytes to disk and returns the path; otherwise it returns the text (if printable) or a hex preview. Run list_resources first to get names.",
				Schema.Object(
					("module", Schema.Str("Module file path, or file name if open in dnSpy"), true),
					("name", Schema.Str("Resource name (exact, from list_resources)"), true),
					("save_path", Schema.Str("Optional file path to write the raw bytes to"), false)),
				ExtractResource);
		}

		string Decompile(JObject args) {
			var module = ReqStr(args, "module");
			var method = (string?)args["method"];
			var type = (string?)args["type"];
			var token = (string?)args["token"];
			var format = (string?)args["format"];

			lock (metadataLock) {
				var mod = MetadataResolver.ResolveModule(documentService.Value, module);
				var decompiler = DecompilerFor(format);
				using var cts = new CancellationTokenSource(DecompileTimeoutMs);
				var ctx = new DecompilationContext { CancellationToken = cts.Token };

				if (token is not null)
					return Truncate(DecompileOne(decompiler, ResolveToken(mod, token), ctx));
				if (!string.IsNullOrEmpty(method)) {
					var parts = MetadataResolver.ResolveMethods(mod, method!)
						.Select(m => DecompileOne(decompiler, m, ctx));
					return Truncate(string.Join(Environment.NewLine + Environment.NewLine, parts));
				}
				if (!string.IsNullOrEmpty(type)) {
					var td = MetadataResolver.FindType(mod, type!)
						?? throw new InvalidOperationException($"type not found: {type}");
					return Truncate(DecompileOne(decompiler, td, ctx));
				}
				throw new ArgumentException("provide one of 'method', 'type' or 'token'");
			}
		}

		static string DecompileOne(IDecompiler decompiler, IMemberDef member, DecompilationContext ctx) {
			var output = new StringBuilderDecompilerOutput();
			switch (member) {
			case MethodDef m: decompiler.Decompile(m, output, ctx); break;
			case TypeDef t: decompiler.Decompile(t, output, ctx); break;
			case FieldDef f: decompiler.Decompile(f, output, ctx); break;
			case PropertyDef p: decompiler.Decompile(p, output, ctx); break;
			case EventDef e: decompiler.Decompile(e, output, ctx); break;
			default:
				throw new InvalidOperationException(
					$"token resolves to {member?.GetType().Name ?? "nothing"}, which is not a decompilable member");
			}
			return output.ToString();
		}

		static IMemberDef ResolveToken(ModuleDef module, string token) {
			var raw = ParseUInt(token, "token");
			if (module is not ModuleDefMD md)
				throw new InvalidOperationException("this module has no metadata token table");
			return md.ResolveToken(raw) as IMemberDef
				?? throw new InvalidOperationException($"no member with token 0x{raw:X8}");
		}

		string ListTypes(JObject args) {
			var module = ReqStr(args, "module");
			var filter = (string?)args["filter"];
			var max = Clamp((int?)args["max"] ?? 500, 1, MaxListItems);

			lock (metadataLock) {
				var mod = MetadataResolver.ResolveModule(documentService.Value, module);
				var rx = string.IsNullOrEmpty(filter) ? null : Wildcard(filter!);
				var arr = new JArray();
				long total = 0;
				foreach (var t in mod.GetTypes()) {
					var full = t.FullName;
					if (rx is not null && !rx.IsMatch(full))
						continue;
					total++;
					if (arr.Count < max) {
						arr.Add(new JObject {
							["name"] = full,
							["token"] = Token(t.MDToken),
							["kind"] = TypeKind(t),
							["methods"] = t.Methods.Count,
						});
					}
				}
				return Json(new JObject {
					["module"] = mod.Name?.String,
					["matched"] = total,
					["returned"] = arr.Count,
					["types"] = arr,
				});
			}
		}

		string ListMethods(JObject args) {
			var module = ReqStr(args, "module");
			var type = ReqStr(args, "type");

			lock (metadataLock) {
				var mod = MetadataResolver.ResolveModule(documentService.Value, module);
				var td = MetadataResolver.FindType(mod, type)
					?? throw new InvalidOperationException($"type not found: {type}");
				var arr = new JArray(td.Methods.Select(m => (object)new JObject {
					["name"] = m.Name.String,
					["token"] = Token(m.MDToken),
					["static"] = m.IsStatic,
					["signature"] = m.FullName,
				}).ToArray());
				return Json(new JObject { ["type"] = td.FullName, ["methods"] = arr });
			}
		}

		string Search(JObject args) {
			var module = ReqStr(args, "module");
			var query = ReqStr(args, "query");
			var kind = ((string?)args["kind"] ?? "all").ToLowerInvariant();
			var max = Clamp((int?)args["max"] ?? 200, 1, MaxListItems);
			var wantNames = kind is "all" or "names";
			var wantStrings = kind is "all" or "strings";
			if (!wantNames && !wantStrings)
				throw new ArgumentException("'kind' must be names, strings or all");

			lock (metadataLock) {
				var mod = MetadataResolver.ResolveModule(documentService.Value, module);
				var rx = wantNames ? Wildcard(query) : null;
				var hits = new JArray();

				foreach (var t in mod.GetTypes()) {
					if (wantNames && NameMatches(rx!, t.FullName, t.Name))
						Add(hits, max, "type", t.FullName, t.MDToken, null);
					foreach (var f in t.Fields) {
						if (hits.Count >= max) break;
						var dotted = Dotted(f);
						if (wantNames && NameMatches(rx!, dotted, f.Name))
							Add(hits, max, "field", dotted, f.MDToken, null);
					}
					foreach (var m in t.Methods) {
						if (hits.Count >= max) break;
						var dotted = Dotted(m);
						if (wantNames && NameMatches(rx!, dotted, m.Name))
							Add(hits, max, "method", dotted, m.MDToken, null);
						if (wantStrings) {
							foreach (var s in StringLiterals(m)) {
								if (s.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
									continue;
								Add(hits, max, "string", dotted, m.MDToken, s);
								if (hits.Count >= max) break;
							}
						}
					}
					if (hits.Count >= max) break;
				}
				return Json(new JObject { ["query"] = query, ["kind"] = kind, ["hits"] = hits });
			}
		}

		// dnlib's member FullName is a full signature ("RetType NS.Type::Method(args)"), which a
		// dotted query like 'NS.Type.Method' or a bare 'Method' won't match. Match against the friendly
		// dotted form and the simple name instead — the same forms decompile/bp_add accept.
		static bool NameMatches(Regex rx, string dottedFullName, string simpleName) =>
			rx.IsMatch(dottedFullName) || rx.IsMatch(simpleName);

		static void Add(JArray hits, int max, string kind, string name, MDToken token, string? value) {
			if (hits.Count >= max) return;
			var o = new JObject { ["kind"] = kind, ["name"] = name, ["token"] = Token(token) };
			if (value is not null)
				o["value"] = value;
			hits.Add(o);
		}

		string FindReferences(JObject args) {
			var module = ReqStr(args, "module");
			var method = (string?)args["method"];
			var field = (string?)args["field"];
			var type = (string?)args["type"];
			var token = (string?)args["token"];
			var scope = ((string?)args["scope"] ?? "module").ToLowerInvariant();
			var access = ((string?)args["access"] ?? "all").ToLowerInvariant();
			var max = Clamp((int?)args["max"] ?? 200, 1, MaxListItems);

			lock (metadataLock) {
				var mod = MetadataResolver.ResolveModule(documentService.Value, module);
				var modules = ModulesForScope(mod, scope);

				if (!string.IsNullOrEmpty(field))
					return FieldReferences(ResolveFields(mod, field!), modules, scope, access, max);
				if (!string.IsNullOrEmpty(type))
					return TypeReferences(FindTypeOrThrow(mod, type!), modules, scope, max);
				if (token is not null) {
					return ResolveToken(mod, token) switch {
						MethodDef md => MethodCallers(new[] { md }, modules, scope, max),
						FieldDef fd => FieldReferences(new[] { fd }, modules, scope, access, max),
						TypeDef td => TypeReferences(td, modules, scope, max),
						_ => throw new InvalidOperationException("token must resolve to a method, field or type"),
					};
				}
				if (!string.IsNullOrEmpty(method))
					return MethodCallers(MetadataResolver.ResolveMethods(mod, method!), modules, scope, max);
				throw new ArgumentException("provide 'method', 'field', 'type' or 'token'");
			}
		}

		// Methods that call any of the target methods (the original find_references behaviour).
		static string MethodCallers(MethodDef[] targets, List<ModuleDef> modules, string scope, int max) {
			var callers = new JArray();
			long scanned = 0;
			foreach (var m in modules.SelectMany(EnumerateMethods)) {
				scanned++;
				if (!m.HasBody)
					continue;
				foreach (var instr in m.Body.Instructions) {
					if (!IsCall(instr.OpCode.Code) || instr.Operand is not IMethod called || called.IsField)
						continue;
					if (!targets.Any(t => Same(called, t)))
						continue;
					callers.Add(new JObject {
						["caller"] = m.FullName,
						["token"] = Token(m.MDToken),
						["callee"] = called.FullName,
						["ilOffset"] = "0x" + instr.Offset.ToString("X"),
					});
					break; // one hit per caller method is enough
				}
				if (callers.Count >= max)
					break;
			}
			return Json(new JObject {
				["target"] = string.Join(", ", targets.Select(t => t.FullName)),
				["scope"] = scope,
				["scannedMethods"] = scanned,
				["callers"] = callers,
			});
		}

		// Methods that read/write any of the target fields. Mirrors dnSpy's FieldAccessNode.
		static string FieldReferences(FieldDef[] targets, List<ModuleDef> modules, string scope, string access, int max) {
			var wantReads = access is "all" or "reads";
			var wantWrites = access is "all" or "writes";
			if (!wantReads && !wantWrites)
				throw new ArgumentException("'access' must be reads, writes or all");
			var refs = new JArray();
			long scanned = 0;
			foreach (var m in modules.SelectMany(EnumerateMethods)) {
				scanned++;
				if (!m.HasBody)
					continue;
				foreach (var instr in m.Body.Instructions) {
					var acc = FieldAccess(instr.OpCode.Code);
					if (acc is null || instr.Operand is not IField f)
						continue;
					var show = acc switch { "read" => wantReads, "write" => wantWrites, _ => true };
					if (!show || !targets.Any(t => SameField(f, t)))
						continue;
					refs.Add(new JObject {
						["method"] = Dotted(m),
						["token"] = Token(m.MDToken),
						["access"] = acc,
						["field"] = f.FullName,
						["ilOffset"] = "0x" + instr.Offset.ToString("X"),
					});
					break; // one row per method
				}
				if (refs.Count >= max)
					break;
			}
			return Json(new JObject {
				["target"] = string.Join(", ", targets.Select(t => t.FullName)),
				["targetKind"] = "field",
				["scope"] = scope,
				["scannedMethods"] = scanned,
				["references"] = refs,
			});
		}

		// Methods that use a target type — instantiate/cast/call/field-access/local/catch. Mirrors TypeUsedByNode.
		static string TypeReferences(TypeDef target, List<ModuleDef> modules, string scope, int max) {
			var refs = new JArray();
			long scanned = 0;
			foreach (var m in modules.SelectMany(EnumerateMethods)) {
				scanned++;
				if (m.HasBody && UsesType(m, target)) {
					refs.Add(new JObject { ["method"] = Dotted(m), ["token"] = Token(m.MDToken) });
					if (refs.Count >= max)
						break;
				}
			}
			return Json(new JObject {
				["target"] = target.FullName,
				["targetKind"] = "type",
				["scope"] = scope,
				["scannedMethods"] = scanned,
				["references"] = refs,
			});
		}

		static FieldDef[] ResolveFields(ModuleDef mod, string fullName) {
			var idx = fullName.LastIndexOf('.');
			if (idx <= 0)
				throw new ArgumentException("field must be fully qualified, e.g. 'Namespace.Type.Field'");
			var typeName = fullName.Substring(0, idx);
			var type = MetadataResolver.FindType(mod, typeName)
				?? throw new InvalidOperationException($"type not found: {typeName}");
			var name = fullName.Substring(idx + 1);
			var fields = type.Fields.Where(f => f.Name == name).ToArray();
			if (fields.Length == 0)
				throw new InvalidOperationException($"field not found: {name} in {type.FullName}");
			return fields;
		}

		static TypeDef FindTypeOrThrow(ModuleDef mod, string type) =>
			MetadataResolver.FindType(mod, type) ?? throw new InvalidOperationException($"type not found: {type}");

		// Field-access classification: read / write / ref (address or token, usable either way), or null.
		static string? FieldAccess(Code code) =>
			code is Code.Ldfld or Code.Ldsfld ? "read" :
			code is Code.Stfld or Code.Stsfld ? "write" :
			code is Code.Ldflda or Code.Ldsflda or Code.Ldtoken ? "ref" :
			null;

		static bool SameField(IField f, FieldDef target) =>
			f.Name == target.Name && f.ResolveFieldDef() is FieldDef fd && (fd == target || FieldKey(fd) == FieldKey(target));

		static string FieldKey(FieldDef f) =>
			f.MDToken.Raw.ToString("X8") + "@" + (f.Module?.Location?.ToLowerInvariant() ?? "");

		// A pragmatic "type is used in this body": any type/field/method operand whose (declaring) type is the
		// target, plus locals and catch types. Does not recurse into generic arguments (a List<T> param won't match).
		static bool UsesType(MethodDef m, TypeDef target) {
			foreach (var instr in m.Body.Instructions) {
				ITypeDefOrRef? ot = instr.Operand switch {
					ITypeDefOrRef tr => tr,
					IField fr => fr.DeclaringType,
					IMethod mr => mr.DeclaringType,
					_ => null,
				};
				if (ot is not null && SameType(ot, target))
					return true;
			}
			if (m.Body.HasVariables)
				foreach (var v in m.Body.Variables)
					if (v.Type?.ToTypeDefOrRef() is ITypeDefOrRef vt && SameType(vt, target))
						return true;
			foreach (var eh in m.Body.ExceptionHandlers)
				if (eh.CatchType is not null && SameType(eh.CatchType, target))
					return true;
			return false;
		}

		static bool SameType(ITypeDefOrRef t, TypeDef target) => new SigComparer().Equals(target, t.GetScopeType());

		static IEnumerable<MethodDef> EnumerateMethods(ModuleDef mod) {
			foreach (var t in mod.GetTypes())
				foreach (var m in t.Methods)
					yield return m;
		}

		// Resolve the target method(s) a find_* tool operates on: a metadata token (takes precedence) or a
		// fully-qualified name (every overload). Shared by find_references and find_implementations.
		static MethodDef[] ResolveTargets(ModuleDef mod, string? method, string? token) {
			if (token is not null)
				return new[] { ResolveToken(mod, token) as MethodDef
					?? throw new InvalidOperationException("token does not resolve to a method") };
			if (!string.IsNullOrEmpty(method))
				return MetadataResolver.ResolveMethods(mod, method!);
			throw new ArgumentException("provide 'method' or 'token'");
		}

		// The modules a 'scope' argument selects: just the target's module, or every assembly open in dnSpy.
		List<ModuleDef> ModulesForScope(ModuleDef mod, string scope) =>
			scope == "open"
				? documentService.Value.GetDocuments().Select(d => d.ModuleDef).OfType<ModuleDef>().Distinct().ToList()
				: new List<ModuleDef> { mod };

		// The string literals (ldstr operands) in a method body — the shared scan behind search and extract_iocs.
		static IEnumerable<string> StringLiterals(MethodDef m) {
			if (!m.HasBody)
				yield break;
			foreach (var instr in m.Body.Instructions)
				if (instr.OpCode.Code == Code.Ldstr && instr.Operand is string s)
					yield return s;
		}

		// The friendly dotted member name ("NS.Type.Member"), as decompile/bp_add accept it.
		static string Dotted(IMemberDef m) => (m.DeclaringType?.FullName ?? "") + "." + m.Name;

		// A MethodDef's cross-module identity: its token is unique only within a module, so pair it with the
		// module location. Used both to compare methods (SameDef) and to dedup them (the find_implementations seen set).
		static string MethodKey(MethodDef m) =>
			m.MDToken.Raw.ToString("X8") + "@" + (m.Module?.Location?.ToLowerInvariant() ?? "");

		// A called reference matches the target when it resolves to the same MethodDef — compared by
		// token and defining module so a MemberRef from another module still lines up (the pattern
		// dnSpy's own "used by" analyzer uses). The name prefilter keeps the resolve off the hot path.
		static bool Same(IMethod called, MethodDef target) =>
			called.Name == target.Name && called.ResolveMethodDef() is MethodDef md && SameDef(md, target);

		static bool IsCall(Code code) =>
			code is Code.Call or Code.Callvirt or Code.Newobj or Code.Ldftn or Code.Ldvirtftn;

		string FindImplementations(JObject args) {
			var module = ReqStr(args, "module");
			var method = (string?)args["method"];
			var token = (string?)args["token"];
			var scope = ((string?)args["scope"] ?? "module").ToLowerInvariant();
			var max = Clamp((int?)args["max"] ?? 200, 1, MaxListItems);

			lock (metadataLock) {
				var mod = MetadataResolver.ResolveModule(documentService.Value, module);
				var targets = ResolveTargets(mod, method, token);
				var modules = ModulesForScope(mod, scope);

				var impls = new JArray();
				var seen = new HashSet<string>();
				long scannedTypes = 0;
				foreach (var type in modules.SelectMany(m => m.GetTypes())) {
					scannedTypes++;
					foreach (var target in targets) {
						if (MatchImplementation(type, target) is not { } match)
							continue;
						var (impl, kind) = match;
						if (!seen.Add(MethodKey(impl)))
							continue;
						impls.Add(new JObject {
							["type"] = type.FullName,
							["method"] = impl.FullName,
							["token"] = Token(impl.MDToken),
							["kind"] = kind,
							["implements"] = target.FullName,
						});
						if (impls.Count >= max) break;
					}
					if (impls.Count >= max) break;
				}
				return Json(new JObject {
					["target"] = string.Join(", ", targets.Select(t => t.FullName)),
					["targetKind"] = TargetOverrideKind(targets[0]),
					["scope"] = scope,
					["scannedTypes"] = scannedTypes,
					["implementations"] = impls,
				});
			}
		}

		// Mirrors dnSpy's own analyzer: InterfaceMethodImplementedByNode for interface methods and
		// MethodOverridesNode ("Overridden By") for virtual/abstract class methods. Returns the one
		// implementing/overriding method in `type` and how it relates to `target`, or null.
		static (MethodDef method, string kind)? MatchImplementation(TypeDef type, MethodDef target) {
			var declType = target.DeclaringType;
			if (declType is null)
				return null;

			if (declType.IsInterface) {
				if (type.IsInterface)
					return null; // an interface method's implementers are concrete types
				// Explicit implementation (.override / MethodImpl) is unambiguous, so it wins.
				foreach (var m in type.Methods) {
					if (!CanImplement(m))
						continue;
					if (m.HasOverrides && m.Overrides.Any(o => ResolvesTo(o, target)))
						return (m, "explicit");
				}
				// Implicit implementation: the type (or a base) declares the interface, and a method
				// matches by name and generic-aware signature.
				var ifaceCtx = GetInterfaceContext(type, declType);
				if (ifaceCtx is not null) {
					foreach (var m in type.Methods) {
						if (!CanImplement(m))
							continue;
						if (m.Name != target.Name)
							continue;
						if (TypesHierarchyHelpers.MatchInterfaceMethod(m, target, ifaceCtx))
							return (m, "interface");
					}
				}
				return null;
			}

			if (target.IsVirtual || target.IsAbstract) {
				if (!TypesHierarchyHelpers.IsBaseType(declType, type, resolveTypeArguments: false))
					return null;
				foreach (var m in type.Methods) {
					if (TypesHierarchyHelpers.IsBaseMethod(target, m)) {
						var hides = !m.IsVirtual ^ m.IsNewSlot;
						return (m, hides ? "hides" : "override");
					}
					if (m.HasOverrides && m.Overrides.Any(o => ResolvesTo(o, target)))
						return (m, "explicit");
				}
			}
			return null;
		}

		// An implementing/overriding method is virtual or static, and not itself abstract.
		static bool CanImplement(MethodDef m) => (m.IsVirtual || m.IsStatic) && !m.IsAbstract;

		static bool ResolvesTo(MethodOverride o, MethodDef target) =>
			o.MethodDeclaration.ResolveMethodDef() is MethodDef md && SameDef(md, target);

		static bool SameDef(MethodDef a, MethodDef b) => a == b || MethodKey(a) == MethodKey(b);

		// The (possibly generic) interface reference on `type` or one of its base types that corresponds
		// to `ifaceDef`, so MatchInterfaceMethod has the right generic context. Mirrors the analyzer's
		// InterfaceMethodImplementedByNode.GetInterface.
		static ITypeDefOrRef? GetInterfaceContext(TypeDef type, TypeDef ifaceDef) {
			foreach (var t in TypesHierarchyHelpers.GetTypeAndBaseTypes(type)) {
				var td = t.Resolve();
				if (td is null)
					break;
				foreach (var ii in td.Interfaces) {
					if (new SigComparer().Equals(ii.Interface.GetScopeType(), ifaceDef))
						return ii.Interface;
				}
			}
			return null;
		}

		static string TargetOverrideKind(MethodDef t) =>
			t.DeclaringType?.IsInterface == true ? "interface" :
			t.IsAbstract ? "abstract" :
			t.IsVirtual ? "virtual" :
			"non-virtual";

		string ExtractIocs(JObject args) {
			var module = ReqStr(args, "module");
			var wanted = ParseCategories((string?)args["categories"]);
			var max = Clamp((int?)args["max"] ?? 500, 1, MaxListItems);

			lock (metadataLock) {
				var mod = MetadataResolver.ResolveModule(documentService.Value, module);
				var iocs = new JArray();
				var seen = new Dictionary<string, JObject>();

				foreach (var m in EnumerateMethods(mod)) {
					if (iocs.Count >= max) break;
					// Built on demand: most methods yield no IOC, and dnlib rebuilds FullName on each access.
					string? dotted = null;

					if (wanted.Contains("pinvoke") && m.ImplMap is ImplMap map) {
						var dll = map.Module?.Name.String ?? "?";
						var entry = UTF8String.IsNullOrEmpty(map.Name) ? m.Name.String : map.Name.String;
						AddIoc(iocs, seen, max, "pinvoke", dll + "!" + entry, dotted ??= Dotted(m), m.MDToken);
					}

					foreach (var s in StringLiterals(m)) {
						if (s.Length == 0) continue;
						foreach (var (cat, value) in ScanString(s, wanted)) {
							AddIoc(iocs, seen, max, cat, value, dotted ??= Dotted(m), m.MDToken);
							if (iocs.Count >= max) break;
						}
						if (iocs.Count >= max) break;
					}
				}

				var countObj = new JObject();
				foreach (var g in iocs.Cast<JObject>().GroupBy(i => (string)i["category"]!).OrderBy(g => g.Key))
					countObj[g.Key] = g.Count();
				var catArr = new JArray();
				foreach (var c in wanted.OrderBy(x => x))
					catArr.Add(c);
				return Json(new JObject {
					["module"] = mod.Name?.String,
					["categories"] = catArr,
					["counts"] = countObj,
					["returned"] = iocs.Count,
					["iocs"] = iocs,
				});
			}
		}

		static HashSet<string> ParseCategories(string? arg) {
			if (string.IsNullOrWhiteSpace(arg))
				return new HashSet<string>(DefaultCategories());
			var set = new HashSet<string>();
			foreach (var raw in arg!.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)) {
				var c = raw.Trim().ToLowerInvariant();
				if (Array.IndexOf(IocCategories, c) < 0)
					throw new ArgumentException($"unknown category '{c}'; valid: {string.Join(", ", IocCategories)}");
				set.Add(c);
			}
			return set.Count == 0 ? new HashSet<string>(DefaultCategories()) : set;
		}

		static IEnumerable<(string cat, string value)> ScanString(string s, HashSet<string> wanted) {
			foreach (var (name, rx) in IocPatterns)
				if (wanted.Contains(name))
					foreach (Match m in rx.Matches(s))
						yield return (name, m.Value);
		}

		static void AddIoc(JArray iocs, Dictionary<string, JObject> seen,
				int max, string category, string value, string method, MDToken token) {
			var key = category + " " + value;
			if (seen.TryGetValue(key, out var existing)) {
				existing["occurrences"] = (int)existing["occurrences"]! + 1;
				return;
			}
			if (iocs.Count >= max)
				return;
			var o = new JObject {
				["category"] = category,
				["value"] = value,
				["method"] = method,
				["token"] = Token(token),
				["occurrences"] = 1,
			};
			seen[key] = o;
			iocs.Add(o);
		}

		// One row per string-literal IOC category, in match order (pinvoke is separate — it is read from each
		// method's ImplMap, not a string regex). Deliberately conservative patterns: octet-validated IPv4,
		// drive/UNC-anchored paths and HK*-anchored registry keys, so a version string or a bare token is not
		// reported as an IOC. base64 matches any long base64 run, so it is noisy and opt-in (NoisyCategory).
		static readonly (string name, Regex rx)[] IocPatterns = {
			("url",      new(@"\b(?:https?|ftp)://[^\s""'<>|\\]+", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
			("ip",       new(@"\b(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)\b", RegexOptions.Compiled)),
			("registry", new(@"(?:HKEY_[A-Z_]+|HKLM|HKCU|HKCR|HKU|HKCC)(?:\\[^\s""'<>|]+)+", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
			("path",     new(@"(?:[A-Za-z]:\\|\\\\)[^\s""'<>|*?]+", RegexOptions.Compiled)),
			("email",    new(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", RegexOptions.Compiled)),
			(NoisyCategory, new(@"[A-Za-z0-9+/]{32,}={0,2}", RegexOptions.Compiled)),
		};
		const string NoisyCategory = "base64";
		// Every category, including the non-regex pinvoke; the default sweep is all of them but the noisy one.
		static readonly string[] IocCategories = IocPatterns.Select(p => p.name).Append("pinvoke").ToArray();
		static IEnumerable<string> DefaultCategories() => IocCategories.Where(c => c != NoisyCategory);

		string TypeHierarchy(JObject args) {
			var module = ReqStr(args, "module");
			var type = (string?)args["type"];
			var token = (string?)args["token"];
			var direction = ((string?)args["direction"] ?? "both").ToLowerInvariant();
			var scope = ((string?)args["scope"] ?? "module").ToLowerInvariant();
			var max = Clamp((int?)args["max"] ?? 500, 1, MaxListItems);
			var wantBase = direction is "both" or "base";
			var wantDerived = direction is "both" or "derived";
			if (!wantBase && !wantDerived)
				throw new ArgumentException("'direction' must be base, derived or both");

			lock (metadataLock) {
				var mod = MetadataResolver.ResolveModule(documentService.Value, module);
				var td = ResolveTypeTarget(mod, type, token);
				var result = new JObject { ["type"] = td.FullName, ["token"] = Token(td.MDToken), ["kind"] = TypeKind(td) };

				if (wantBase) {
					var bases = new JArray();
					for (var bt = td.BaseType; bt is not null; ) {
						bases.Add(new JObject { ["name"] = bt.FullName, ["token"] = bt is TypeDef btd ? Token(btd.MDToken) : null });
						var rd = bt.ResolveTypeDef();
						if (rd is null) break;
						bt = rd.BaseType;
					}
					result["baseTypes"] = bases;
					result["interfaces"] = new JArray(td.Interfaces.Select(i => (object)i.Interface.FullName).ToArray());
				}

				if (wantDerived) {
					var derived = new JArray();
					long scanned = 0;
					foreach (var t in ModulesForScope(mod, scope).SelectMany(m => m.GetTypes())) {
						scanned++;
						if (SameType(t, td))
							continue;
						if (TypesHierarchyHelpers.IsBaseType(td, t, resolveTypeArguments: false) || ImplementsInterface(t, td)) {
							derived.Add(new JObject { ["name"] = t.FullName, ["token"] = Token(t.MDToken), ["kind"] = TypeKind(t) });
							if (derived.Count >= max) break;
						}
					}
					result["scannedTypes"] = scanned;
					result["derivedTypes"] = derived;
				}
				return Json(result);
			}
		}

		static TypeDef ResolveTypeTarget(ModuleDef mod, string? type, string? token) {
			if (token is not null)
				return ResolveToken(mod, token) as TypeDef
					?? throw new InvalidOperationException("token does not resolve to a type");
			if (!string.IsNullOrEmpty(type))
				return FindTypeOrThrow(mod, type!);
			throw new ArgumentException("provide 'type' or 'token'");
		}

		// Does `t` (or a base type) implement interface `iface`?
		static bool ImplementsInterface(TypeDef t, TypeDef iface) {
			if (!iface.IsInterface)
				return false;
			foreach (var st in TypesHierarchyHelpers.GetTypeAndBaseTypes(t)) {
				var std = st.Resolve();
				if (std is null)
					break;
				foreach (var ii in std.Interfaces)
					if (new SigComparer().Equals(ii.Interface.GetScopeType(), iface))
						return true;
			}
			return false;
		}

		string ListResources(JObject args) {
			var module = ReqStr(args, "module");
			lock (metadataLock) {
				var mod = MetadataResolver.ResolveModule(documentService.Value, module);
				var arr = new JArray();
				foreach (var r in mod.Resources) {
					var o = new JObject {
						["name"] = r.Name?.String,
						["type"] = r.ResourceType.ToString(),
						["visibility"] = r.Attributes.ToString(),
					};
					if (r is EmbeddedResource er)
						o["length"] = er.Length;
					arr.Add(o);
				}
				return Json(new JObject { ["module"] = mod.Name?.String, ["count"] = arr.Count, ["resources"] = arr });
			}
		}

		string ExtractResource(JObject args) {
			var module = ReqStr(args, "module");
			var name = ReqStr(args, "name");
			var savePath = (string?)args["save_path"];
			lock (metadataLock) {
				var mod = MetadataResolver.ResolveModule(documentService.Value, module);
				if (mod.Resources.FirstOrDefault(r => r.Name?.String == name) is not EmbeddedResource res)
					throw new InvalidOperationException($"no embedded resource named '{name}' (or it is not an embedded resource)");
				var bytes = res.CreateReader().ToArray();
				if (!string.IsNullOrEmpty(savePath)) {
					System.IO.File.WriteAllBytes(savePath!, bytes);
					return Json(new JObject { ["name"] = name, ["length"] = bytes.Length, ["savedTo"] = savePath });
				}
				var o = new JObject { ["name"] = name, ["length"] = bytes.Length };
				if (LooksTextual(bytes))
					o["text"] = System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, MaxDecompileChars));
				else
					o["hexHead"] = BitConverter.ToString(bytes, 0, Math.Min(bytes.Length, 256)).Replace("-", "");
				return Json(o);
			}
		}

		// A cheap "is this printable text" heuristic for inline resource preview.
		static bool LooksTextual(byte[] b) {
			int n = Math.Min(b.Length, 512);
			for (int i = 0; i < n; i++) {
				var c = b[i];
				if (c == 0 || c < 0x09 || (c > 0x0D && c < 0x20 && c != 0x1B))
					return false;
			}
			return true;
		}

		IDecompiler DecompilerFor(string? format) {
			if (string.IsNullOrEmpty(format) || string.Equals(format, "csharp", StringComparison.OrdinalIgnoreCase))
				return decompilerService.Value.Find(DecompilerConstants.LANGUAGE_CSHARP) ?? decompilerService.Value.Decompiler;
			if (string.Equals(format, "il", StringComparison.OrdinalIgnoreCase))
				return decompilerService.Value.Find(DecompilerConstants.LANGUAGE_IL)
					?? throw new InvalidOperationException("the IL disassembler is not available");
			throw new ArgumentException("'format' must be 'csharp' or 'il'");
		}

		static string Token(MDToken token) => "0x" + token.Raw.ToString("X8");

		static string TypeKind(TypeDef t) =>
			t.IsInterface ? "interface" :
			t.IsEnum ? "enum" :
			t.IsValueType ? "struct" :
			IsDelegate(t) ? "delegate" :
			"class";

		static bool IsDelegate(TypeDef t) {
			var baseName = t.BaseType?.FullName;
			return baseName == "System.MulticastDelegate" || baseName == "System.Delegate";
		}

		static Regex Wildcard(string pattern) =>
			new Regex("^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase);

		static string Truncate(string text) =>
			text.Length <= MaxDecompileChars
				? text
				: text.Substring(0, MaxDecompileChars) + Environment.NewLine + Environment.NewLine +
					$"/* … truncated at {MaxDecompileChars} characters; narrow to a single method/type */";

		static string ReqStr(JObject args, string name) =>
			(string?)args[name] ?? throw new ArgumentException($"'{name}' is required");
	}
}
