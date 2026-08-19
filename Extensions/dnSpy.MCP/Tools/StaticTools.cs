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
				"Decompile a method or type to C#. Identify it by fully-qualified name (all overloads, for a method) or by metadata token. Works on any assembly given a full path or already open in dnSpy; no debug session required.",
				Schema.Object(
					("module", Schema.Str("Module file path, or file name if already open in dnSpy"), true),
					("method", Schema.Str("Fully-qualified method, e.g. 'MyApp.Program.Main' (decompiles every overload)"), false),
					("type", Schema.Str("Fully-qualified type, e.g. 'MyApp.Program' (decompiles the whole type)"), false),
					("token", Schema.Str("Metadata token of a method or type, hex (0x06000001) or decimal"), false)),
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
		}

		string Decompile(JObject args) {
			var module = ReqStr(args, "module");
			var method = (string?)args["method"];
			var type = (string?)args["type"];
			var token = (string?)args["token"];

			lock (metadataLock) {
				var mod = MetadataResolver.ResolveModule(documentService.Value, module);
				var decompiler = CSharpDecompiler();
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

		IDecompiler CSharpDecompiler() =>
			decompilerService.Value.Find(DecompilerConstants.LANGUAGE_CSHARP) ?? decompilerService.Value.Decompiler;

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
