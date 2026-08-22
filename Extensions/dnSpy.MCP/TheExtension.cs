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

using System.Collections.Generic;
using System.ComponentModel.Composition;
using dnSpy.Contracts.Extension;

namespace dnSpy.MCP {
	[ExportExtension]
	sealed class TheExtension : IExtension {
		readonly McpServerHost host;

		[ImportingConstructor]
		TheExtension(McpServerHost host) => this.host = host;

		public IEnumerable<string> MergedResourceDictionaries {
			get { yield break; }
		}

		public ExtensionInfo ExtensionInfo => new ExtensionInfo {
			ShortDescription = "MCP server exposing dnSpy's debugger to AI agents",
		};

		public void OnEvent(ExtensionEvent @event, object? obj) {
			if (@event == ExtensionEvent.AppExit)
				host.Stop();
		}
	}
}
