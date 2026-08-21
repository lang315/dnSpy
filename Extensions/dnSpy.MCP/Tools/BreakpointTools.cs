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
				"Add an IL-offset method breakpoint. Identify the method by module and metadata token; breakpoints can be set before the process starts (they bind when the module loads). The module must be a full path or already open in dnSpy.",
				Schema.Object(
					("module", Schema.Str("Module file path, or file name if already open in dnSpy"), true),
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
				_ => List(), readOnly: true);

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

			yield return new ToolDef("dbg_run_to",
				"Resume the debugged process and run until it reaches a method (by name or module+token), then pause there. Sets a temporary breakpoint, continues, waits for the hit, and removes the breakpoint. Requires an active session (dbg_start first).",
				Schema.Object(
					("module", Schema.Str("Module file path, or file name if open in dnSpy"), true),
					("method", Schema.Str("Fully-qualified method to run to (every overload)"), false),
					("token", Schema.Str("Method metadata token to run to (hex or decimal)"), false),
					("il_offset", Schema.Str("IL offset within the method (default 0)"), false),
					("timeout_ms", Schema.Int("Max time to wait for the target to be reached (default 30000)"), false)),
				RunTo);
		}

		string Add(JObject args) {
			var module = (string?)args["module"] ?? throw new ArgumentException("'module' is required");
			var token = ParseUInt((string?)args["token"] ?? throw new ArgumentException("'token' is required"), "token");
			var offset = args["il_offset"] is { } o ? ParseUInt((string?)o, "il_offset") : 0u;
			var condition = (string?)args["condition"];
			var enabled = (bool?)args["enabled"] ?? true;
			var hitCount = (int?)args["hit_count"];

			return dbg.Invoke(() => {
				// Derive the ModuleId from the resolved module so it matches the loaded module exactly.
				// ModuleId.Create(string) would resolve a bare file name against dnSpy's working directory
				// and compare on the full path, so a name like 'MyApp.dll' would never bind.
				var moduleId = moduleIdProvider.Value.Create(ResolveModuleDef(module));
				var settings = MakeSettings(enabled, condition);
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

		// internal so the tracepoint tool builds its settings through the exact same path, then adds Trace.
		internal static DbgCodeBreakpointSettings MakeSettings(bool enabled, string? condition) {
			var settings = new DbgCodeBreakpointSettings { IsEnabled = enabled };
			if (!string.IsNullOrEmpty(condition))
				settings.Condition = new DbgCodeBreakpointCondition(DbgCodeBreakpointConditionKind.IsTrue, condition!);
			return settings;
		}

		ModuleDef ResolveModuleDef(string module) =>
			MetadataResolver.ResolveModule(documentService.Value, module);

		static MethodDef[] ResolveMethods(ModuleDef module, string fullName) =>
			MetadataResolver.ResolveMethods(module, fullName);

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

		// Resume and run to a target method, then pause. Uses the target breakpoint's hit-count rising as the
		// "we got there" signal, which sidesteps the RunAll-is-async race (a naive wait for !IsRunning right
		// after RunAll can return on the *previous* pause before the resume takes effect).
		string RunTo(JObject args) {
			var module = (string?)args["module"] ?? throw new ArgumentException("'module' is required");
			var method = (string?)args["method"];
			var tokenStr = (string?)args["token"];
			var offset = args["il_offset"] is { } o ? ParseUInt((string?)o, "il_offset") : 0u;
			var timeout = Clamp((int?)args["timeout_ms"] ?? 30000, 100, 300000);

			var (ids, baseline) = dbg.Invoke(() => {
				if (!dbg.DbgManager.IsDebugging)
					throw new InvalidOperationException("not debugging; start a session first (dbg_start), then run_to");
				var moduleDef = ResolveModuleDef(module);
				var settings = MakeSettings(true, null);
				var made = new List<DbgCodeBreakpoint>();
				if (tokenStr is not null) {
					var bp = bpFactory.Value.Create(moduleIdProvider.Value.Create(moduleDef), ParseUInt(tokenStr, "token"), offset, settings);
					if (bp is not null)
						made.Add(bp);
				}
				else if (!string.IsNullOrEmpty(method)) {
					var infos = ResolveMethods(moduleDef, method!).Select(m => new DbgCodeBreakpointInfo(
						codeLocationFactory.Value.Create(moduleIdProvider.Value.Create(m.Module), m.MDToken.Raw, offset), settings)).ToArray();
					made.AddRange(bpService.Value.Add(infos));
				}
				else
					throw new ArgumentException("provide 'method' or 'token'");
				if (made.Count == 0)
					throw new InvalidOperationException("a breakpoint already exists at the target; remove it, or use dbg_continue + dbg_wait_for_break");
				return (made.Select(b => b.Id).ToArray(), made.Sum(b => hitCountService.Value.GetHitCount(b) ?? 0));
			});

			dbg.DbgManager.RunAll();

			var sw = System.Diagnostics.Stopwatch.StartNew();
			var reached = false;
			while (sw.ElapsedMilliseconds < timeout) {
				if (dbg.Invoke(() => dbg.DbgManager.IsDebugging && dbg.DbgManager.IsRunning == false
						&& ids.Sum(HitCountOf) > baseline)) {
					reached = true;
					break;
				}
				System.Threading.Thread.Sleep(100);
			}

			return dbg.Invoke(() => {
				var totalHits = ids.Sum(HitCountOf);
				foreach (var id in ids)
					bpService.Value.Breakpoints.FirstOrDefault(b => b.Id == id)?.Remove();
				var result = new JObject {
					["reached"] = reached,
					["breakpointsSet"] = ids.Length,
					["totalHits"] = totalHits,
					["isPaused"] = dbg.DbgManager.IsDebugging && dbg.DbgManager.IsRunning == false,
				};
				if (!reached)
					result["note"] = "target not reached within the timeout (the process may have exited, or the location was never executed)";
				return Json(result);
			});
		}

		// Live hit count of a breakpoint by id (0 if it is gone). Must be called on the debugger dispatcher.
		int HitCountOf(int id) {
			var bp = bpService.Value.Breakpoints.FirstOrDefault(b => b.Id == id);
			return bp is null ? 0 : hitCountService.Value.GetHitCount(bp) ?? 0;
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
