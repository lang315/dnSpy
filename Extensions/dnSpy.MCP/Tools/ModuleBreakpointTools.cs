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
using dnSpy.Contracts.Debugger.Breakpoints.Modules;
using dnSpy.MCP.Server;
using Newtonsoft.Json.Linq;
using static dnSpy.MCP.Tools.JsonUtils;

namespace dnSpy.MCP.Tools {
	/// <summary>Module-load breakpoints: break when a module matching a name pattern is loaded.</summary>
	sealed class ModuleBreakpointTools {
		readonly DbgAccess dbg;
		readonly Lazy<DbgModuleBreakpointsService> service;

		public ModuleBreakpointTools(DbgAccess dbg, Lazy<DbgModuleBreakpointsService> service) {
			this.dbg = dbg;
			this.service = service;
		}

		public IEnumerable<ToolDef> Create() {
			yield return new ToolDef("mbp_add",
				"Add a module-load breakpoint: break when a module whose name matches the pattern is loaded.",
				Schema.Object(
					("module_name", Schema.Str("Module name pattern, case-insensitive, wildcards * and ? allowed"), true),
					("enabled", Schema.Bool("Whether the breakpoint is enabled (default true)"), false)),
				Add);

			yield return new ToolDef("mbp_list",
				"List module-load breakpoints.",
				Schema.Object(),
				_ => List());

			yield return new ToolDef("mbp_remove",
				"Remove a module-load breakpoint by id, or all of them.",
				Schema.Object(
					("id", Schema.Int("Breakpoint id"), false),
					("all", Schema.Bool("Remove every module breakpoint"), false)),
				Remove);
		}

		string Add(JObject args) {
			var moduleName = (string?)args["module_name"] ?? throw new ArgumentException("'module_name' is required");
			var enabled = (bool?)args["enabled"] ?? true;
			return dbg.Invoke(() => {
				var bp = service.Value.Add(new DbgModuleBreakpointSettings {
					IsEnabled = enabled,
					IsLoaded = true,
					ModuleName = moduleName,
				});
				return Json(Describe(bp));
			});
		}

		string List() => dbg.Invoke(() => {
			var arr = new JArray(service.Value.Breakpoints.OrderBy(b => b.Id).Select(Describe).Cast<object>().ToArray());
			return Json(arr);
		});

		string Remove(JObject args) {
			var all = (bool?)args["all"] ?? false;
			return dbg.Invoke(() => {
				if (all) {
					var count = service.Value.Breakpoints.Length;
					service.Value.Clear();
					return $"removed {count} module breakpoint(s)";
				}
				var id = (int?)args["id"] ?? throw new ArgumentException("provide 'id' or 'all'");
				var bp = service.Value.Breakpoints.FirstOrDefault(b => b.Id == id)
					?? throw new InvalidOperationException($"no module breakpoint with id {id}");
				bp.Remove();
				return $"removed module breakpoint {id}";
			});
		}

		static JObject Describe(DbgModuleBreakpoint bp) => new JObject {
			["id"] = bp.Id,
			["enabled"] = bp.IsEnabled,
			["moduleName"] = bp.ModuleName,
		};
	}
}
