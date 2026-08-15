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
using dnSpy.Contracts.Debugger.Breakpoints.Code;
using dnSpy.Contracts.Debugger.DotNet.Breakpoints.Code;
using dnSpy.Contracts.Debugger.DotNet.Code;
using dnSpy.Contracts.Metadata;
using dnSpy.MCP.Server;
using Newtonsoft.Json.Linq;
using static dnSpy.MCP.Tools.JsonUtils;

namespace dnSpy.MCP.Tools {
	/// <summary>IL-offset code breakpoints: add, list, remove, toggle.</summary>
	sealed class BreakpointTools {
		readonly DbgAccess dbg;
		readonly Lazy<DbgCodeBreakpointsService> bpService;
		readonly Lazy<DbgDotNetBreakpointFactory> bpFactory;
		readonly Lazy<DbgCodeBreakpointHitCountService> hitCountService;

		public BreakpointTools(DbgAccess dbg, Lazy<DbgCodeBreakpointsService> bpService,
			Lazy<DbgDotNetBreakpointFactory> bpFactory, Lazy<DbgCodeBreakpointHitCountService> hitCountService) {
			this.dbg = dbg;
			this.bpService = bpService;
			this.bpFactory = bpFactory;
			this.hitCountService = hitCountService;
		}

		public IEnumerable<ToolDef> Create() {
			yield return new ToolDef("bp_add",
				"Add an IL-offset method breakpoint. Identify the method by module name and metadata token; breakpoints can be set before the process starts (they bind when the module loads).",
				Schema.Object(
					("module", Schema.Str("Module file name or path, e.g. 'MyApp.dll'"), true),
					("token", Schema.Str("Method metadata token, hex (0x06000001) or decimal"), true),
					("il_offset", Schema.Str("IL offset into the method body, hex or decimal (default 0)"), false),
					("condition", Schema.Str("C#/VB condition expression; break only when true"), false),
					("enabled", Schema.Bool("Whether the breakpoint is enabled (default true)"), false)),
				Add);

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

			return dbg.Invoke(() => {
				var moduleId = ModuleId.Create(module);
				var settings = new DbgCodeBreakpointSettings { IsEnabled = enabled };
				if (!string.IsNullOrEmpty(condition))
					settings.Condition = new DbgCodeBreakpointCondition(DbgCodeBreakpointConditionKind.IsTrue, condition!);
				var bp = bpFactory.Value.Create(moduleId, token, offset, settings);
				if (bp is null)
					throw new InvalidOperationException("a breakpoint already exists at that location");
				return Json(Describe(bp));
			});
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

		static uint ParseUInt(string? text, string field) {
			if (text is null)
				throw new ArgumentException($"'{field}' is required");
			text = text.Trim();
			var isHex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
			var span = isHex ? text.Substring(2) : text;
			if (uint.TryParse(span, isHex ? NumberStyles.HexNumber : NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
				return value;
			throw new ArgumentException($"'{field}' is not a valid number: {text}");
		}
	}
}
