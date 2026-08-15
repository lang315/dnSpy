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
using dnSpy.Contracts.Debugger.Exceptions;
using dnSpy.MCP.Server;
using Newtonsoft.Json.Linq;

namespace dnSpy.MCP.Tools {
	/// <summary>Configure break-on-exception (first-chance) for CLR exceptions.</summary>
	sealed class ExceptionTools {
		readonly DbgAccess dbg;
		readonly Lazy<DbgExceptionSettingsService> excService;

		public ExceptionTools(DbgAccess dbg, Lazy<DbgExceptionSettingsService> excService) {
			this.dbg = dbg;
			this.excService = excService;
		}

		public IEnumerable<ToolDef> Create() {
			yield return new ToolDef("exc_break",
				"Enable or disable breaking when a CLR exception is thrown (first chance). Omit 'exception' or pass 'all' for every exception.",
				Schema.Object(
					("exception", Schema.Str("Fully-qualified exception type, e.g. System.InvalidOperationException; 'all' or empty = any"), false),
					("enabled", Schema.Bool("true = break on it, false = stop breaking (default true)"), false)),
				Break);
		}

		string Break(JObject args) {
			var exception = (string?)args["exception"];
			var enabled = (bool?)args["enabled"] ?? true;
			var isAll = string.IsNullOrEmpty(exception) || string.Equals(exception, "all", StringComparison.OrdinalIgnoreCase);

			return dbg.Invoke(() => {
				var id = isAll
					? new DbgExceptionId(PredefinedExceptionCategories.DotNet)
					: new DbgExceptionId(PredefinedExceptionCategories.DotNet, exception!);
				var svc = excService.Value;
				if (svc.TryGetSettings(id, out var settings)) {
					var flags = enabled
						? settings.Flags | DbgExceptionDefinitionFlags.StopFirstChance
						: settings.Flags & ~DbgExceptionDefinitionFlags.StopFirstChance;
					svc.Modify(id, new DbgExceptionSettings(flags, settings.Conditions));
				}
				else if (enabled) {
					var def = new DbgExceptionDefinition(id, DbgExceptionDefinitionFlags.StopFirstChance | DbgExceptionDefinitionFlags.StopSecondChance);
					svc.Add(new DbgExceptionSettingsInfo(def, new DbgExceptionSettings(def.Flags)));
				}
				var target = isAll ? "all exceptions" : exception;
				return enabled ? $"break-on-exception enabled: {target}" : $"break-on-exception disabled: {target}";
			});
		}
	}
}
