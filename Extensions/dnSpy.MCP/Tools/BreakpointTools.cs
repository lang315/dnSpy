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
using System.IO;
using System.Linq;
using dnlib.DotNet;
using dnSpy.Contracts.Debugger.Breakpoints.Code;
using dnSpy.Contracts.Debugger.DotNet.Breakpoints.Code;
using dnSpy.Contracts.Debugger.DotNet.Code;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Metadata;
using dnSpy.MCP.Server;
using Newtonsoft.Json.Linq;
using static dnSpy.MCP.Tools.JsonUtils;

namespace dnSpy.MCP.Tools {
	/// <summary>Code breakpoints by IL offset, method name, or source line; add, list, remove, toggle.</summary>
	sealed class BreakpointTools {
		readonly DbgAccess dbg;
		readonly Lazy<DbgCodeBreakpointsService> bpService;
		readonly Lazy<DbgDotNetBreakpointFactory> bpFactory;
		readonly Lazy<DbgCodeBreakpointHitCountService> hitCountService;
		readonly Lazy<DbgDotNetCodeLocationFactory> codeLocationFactory;
		readonly Lazy<IModuleIdProvider> moduleIdProvider;
		readonly Lazy<IDsDocumentService> documentService;

		public BreakpointTools(DbgAccess dbg, Lazy<DbgCodeBreakpointsService> bpService,
			Lazy<DbgDotNetBreakpointFactory> bpFactory, Lazy<DbgCodeBreakpointHitCountService> hitCountService,
			Lazy<DbgDotNetCodeLocationFactory> codeLocationFactory, Lazy<IModuleIdProvider> moduleIdProvider,
			Lazy<IDsDocumentService> documentService) {
			this.dbg = dbg;
			this.bpService = bpService;
			this.bpFactory = bpFactory;
			this.hitCountService = hitCountService;
			this.codeLocationFactory = codeLocationFactory;
			this.moduleIdProvider = moduleIdProvider;
			this.documentService = documentService;
		}

		public IEnumerable<ToolDef> Create() {
			yield return new ToolDef("bp_add",
				"Add an IL-offset method breakpoint. Identify the method by module name and metadata token; breakpoints can be set before the process starts (they bind when the module loads).",
				Schema.Object(
					("module", Schema.Str("Module file name or path, e.g. 'MyApp.dll'"), true),
					("token", Schema.Str("Method metadata token, hex (0x06000001) or decimal"), true),
					("il_offset", Schema.Str("IL offset into the method body, hex or decimal (default 0)"), false),
					("condition", Schema.Str("C#/VB condition expression; break only when true"), false),
					("hit_count", Schema.Int("Break only after the breakpoint has been hit this many times"), false),
					("enabled", Schema.Bool("Whether the breakpoint is enabled (default true)"), false)),
				Add);

			yield return new ToolDef("bp_add_method",
				"Add a breakpoint at the start of a method identified by name (no metadata token needed). Sets a breakpoint on every overload. The module must be a full path or already open in dnSpy.",
				Schema.Object(
					("module", Schema.Str("Module file path, or file name if already open in dnSpy"), true),
					("method", Schema.Str("Fully-qualified method, e.g. 'MyApp.Program.Main'"), true),
					("condition", Schema.Str("C#/VB condition expression; break only when true"), false),
					("enabled", Schema.Bool("Whether the breakpoint is enabled (default true)"), false)),
				AddMethod);

			yield return new ToolDef("bp_add_line",
				"Add a breakpoint at a source line inside a method (requires the module's PDB to be available).",
				Schema.Object(
					("module", Schema.Str("Module file path, or file name if already open in dnSpy"), true),
					("method", Schema.Str("Fully-qualified method containing the line"), true),
					("line", Schema.Int("1-based source line number"), true),
					("enabled", Schema.Bool("Whether the breakpoint is enabled (default true)"), false)),
				AddLine);

			yield return new ToolDef("bp_list",
				"List all code breakpoints with their id, location, enabled state, and live hit count.",
				Schema.Object(),
				_ => List());

			yield return new ToolDef("bp_remove",
				"Remove a breakpoint by id, or all breakpoints.",
				Schema.Object(
					("id", Schema.Int("Breakpoint id to remove"), false),
					("all", Schema.Bool("Remove every breakpoint"), false)),
				Remove);

			yield return new ToolDef("bp_toggle",
				"Enable or disable a breakpoint by id.",
				Schema.Object(
					("id", Schema.Int("Breakpoint id"), true),
					("enabled", Schema.Bool("New enabled state"), true)),
				Toggle);
		}

		string Add(JObject args) {
			var module = (string?)args["module"] ?? throw new ArgumentException("'module' is required");
			var token = ParseUInt((string?)args["token"] ?? throw new ArgumentException("'token' is required"), "token");
			var offset = args["il_offset"] is { } o ? ParseUInt((string?)o, "il_offset") : 0u;
			var condition = (string?)args["condition"];
			var enabled = (bool?)args["enabled"] ?? true;
			var hitCount = (int?)args["hit_count"];

			return dbg.Invoke(() => {
				var moduleId = ModuleId.Create(module);
				var settings = new DbgCodeBreakpointSettings { IsEnabled = enabled };
				if (!string.IsNullOrEmpty(condition))
					settings.Condition = new DbgCodeBreakpointCondition(DbgCodeBreakpointConditionKind.IsTrue, condition!);
				if (hitCount is { } hc && hc > 0)
					settings.HitCount = new DbgCodeBreakpointHitCount(DbgCodeBreakpointHitCountKind.GreaterThanOrEquals, hc);
				var bp = bpFactory.Value.Create(moduleId, token, offset, settings);
				if (bp is null)
					throw new InvalidOperationException("a breakpoint already exists at that location");
				return Json(Describe(bp));
			});
		}

		string AddMethod(JObject args) {
			var module = (string?)args["module"] ?? throw new ArgumentException("'module' is required");
			var methodName = (string?)args["method"] ?? throw new ArgumentException("'method' is required");
			var condition = (string?)args["condition"];
			var enabled = (bool?)args["enabled"] ?? true;

			return dbg.Invoke(() => {
				var moduleDef = ResolveModuleDef(module);
				var methods = ResolveMethods(moduleDef, methodName);
				var settings = MakeSettings(enabled, condition);
				var infos = methods.Select(m => new DbgCodeBreakpointInfo(
					codeLocationFactory.Value.Create(moduleIdProvider.Value.Create(m.Module), m.MDToken.Raw, 0), settings)).ToArray();
				var bps = bpService.Value.Add(infos);
				if (bps.Length == 0)
					throw new InvalidOperationException($"all {infos.Length} matching overload(s) already have a breakpoint");
				return Json(new JArray(bps.Select(Describe).Cast<object>().ToArray()));
			});
		}

		string AddLine(JObject args) {
			var module = (string?)args["module"] ?? throw new ArgumentException("'module' is required");
			var methodName = (string?)args["method"] ?? throw new ArgumentException("'method' is required");
			var line = (int?)args["line"] ?? throw new ArgumentException("'line' is required");
			var enabled = (bool?)args["enabled"] ?? true;

			return dbg.Invoke(() => {
				var moduleDef = ResolveModuleDef(module);
				var methods = ResolveMethods(moduleDef, methodName);
				// Find the first method+instruction whose sequence point starts on the requested line.
				foreach (var m in methods) {
					if (m.Body is null)
						continue;
					foreach (var instr in m.Body.Instructions) {
						var sp = instr.SequencePoint;
						if (sp is not null && sp.StartLine == line) {
							var loc = codeLocationFactory.Value.Create(moduleIdProvider.Value.Create(m.Module), m.MDToken.Raw, instr.Offset);
							var bp = bpService.Value.Add(new DbgCodeBreakpointInfo(loc, MakeSettings(enabled, null)));
							if (bp is null)
								throw new InvalidOperationException("a breakpoint already exists at that location");
							return Json(Describe(bp));
						}
					}
				}
				throw new InvalidOperationException($"no source line {line} found in {methodName} (is the PDB available?)");
			});
		}

		static DbgCodeBreakpointSettings MakeSettings(bool enabled, string? condition) {
			var settings = new DbgCodeBreakpointSettings { IsEnabled = enabled };
			if (!string.IsNullOrEmpty(condition))
				settings.Condition = new DbgCodeBreakpointCondition(DbgCodeBreakpointConditionKind.IsTrue, condition!);
			return settings;
		}

		ModuleDef ResolveModuleDef(string module) {
			IDsDocument? doc;
			if (File.Exists(module))
				doc = documentService.Value.TryGetOrCreate(DsDocumentInfo.CreateDocument(module));
			else
				doc = documentService.Value.GetDocuments().FirstOrDefault(d =>
					string.Equals(Path.GetFileName(d.Filename), module, StringComparison.OrdinalIgnoreCase) ||
					string.Equals(d.ModuleDef?.Name, module, StringComparison.OrdinalIgnoreCase));
			return doc?.ModuleDef
				?? throw new InvalidOperationException($"could not load module '{module}'; pass a full path or open it in dnSpy first");
		}

		static MethodDef[] ResolveMethods(ModuleDef module, string fullName) {
			var idx = fullName.LastIndexOf('.');
			if (idx <= 0)
				throw new ArgumentException("'method' must be fully qualified, e.g. 'Namespace.Type.Method'");
			var typeName = fullName.Substring(0, idx);
			var methodName = fullName.Substring(idx + 1);
			var type = FindType(module, typeName)
				?? throw new InvalidOperationException($"type not found: {typeName}");
			var methods = type.Methods.Where(m => m.Name == methodName).ToArray();
			if (methods.Length == 0)
				throw new InvalidOperationException($"method not found: {methodName} in {typeName}");
			return methods;
		}

		// A nested type's reflection name uses '+', not '.'. The caller passes '.', so progressively
		// turn the rightmost '.' into '+' until the type resolves (handles arbitrary nesting depth).
		static TypeDef? FindType(ModuleDef module, string typeName) {
			var type = module.Find(typeName, isReflectionName: true);
			if (type is not null)
				return type;
			var candidate = typeName;
			int dot;
			while ((dot = candidate.LastIndexOf('.')) > 0) {
				candidate = candidate.Substring(0, dot) + "+" + candidate.Substring(dot + 1);
				type = module.Find(candidate, isReflectionName: true);
				if (type is not null)
					return type;
			}
			return null;
		}

		string List() => dbg.Invoke(() => {
			var arr = new JArray(bpService.Value.Breakpoints
				.Where(b => !b.IsHidden)
				.OrderBy(b => b.Id)
				.Select(Describe)
				.Cast<object>()
				.ToArray());
			return Json(arr);
		});

		string Remove(JObject args) {
			var all = (bool?)args["all"] ?? false;
			return dbg.Invoke(() => {
				if (all) {
					var count = bpService.Value.Breakpoints.Length;
					bpService.Value.Clear();
					return $"removed {count} breakpoint(s)";
				}
				var id = (int?)args["id"] ?? throw new ArgumentException("provide 'id' or 'all'");
				var bp = bpService.Value.Breakpoints.FirstOrDefault(b => b.Id == id)
					?? throw new InvalidOperationException($"no breakpoint with id {id}");
				bp.Remove();
				return $"removed breakpoint {id}";
			});
		}

		string Toggle(JObject args) {
			var id = (int?)args["id"] ?? throw new ArgumentException("'id' is required");
			var enabled = (bool?)args["enabled"] ?? throw new ArgumentException("'enabled' is required");
			return dbg.Invoke(() => {
				var bp = bpService.Value.Breakpoints.FirstOrDefault(b => b.Id == id)
					?? throw new InvalidOperationException($"no breakpoint with id {id}");
				bp.IsEnabled = enabled;
				return $"breakpoint {id} {(enabled ? "enabled" : "disabled")}";
			});
		}

		JObject Describe(DbgCodeBreakpoint bp) {
			var o = new JObject {
				["id"] = bp.Id,
				["enabled"] = bp.IsEnabled,
				["hitCount"] = hitCountService.Value.GetHitCount(bp),
				["boundCount"] = bp.BoundBreakpoints.Length,
			};
			if (bp.Location is DbgDotNetCodeLocation loc) {
				o["module"] = loc.Module.ModuleName;
				o["token"] = "0x" + loc.Token.ToString("X8");
				o["offset"] = "0x" + loc.Offset.ToString("X");
			}
			else
				o["location"] = bp.Location.Type;
			if (bp.Condition is { } cond)
				o["condition"] = cond.Condition;
			return o;
		}
	}
}
