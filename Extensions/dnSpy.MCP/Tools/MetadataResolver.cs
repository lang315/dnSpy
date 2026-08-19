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
using System.IO;
using System.Linq;
using dnlib.DotNet;
using dnSpy.Contracts.Documents;

namespace dnSpy.MCP.Tools {
	/// <summary>
	/// Resolves a caller-supplied module and member name to dnlib metadata. Shared so the breakpoint
	/// tools and the static-analysis tools agree on exactly what "module X" and "Namespace.Type.Method"
	/// mean — a breakpoint set by name and a decompile by the same name must land on the same method.
	/// </summary>
	static class MetadataResolver {
		/// <summary>The module for a path (loaded if needed) or the file/module name of one already open.</summary>
		public static ModuleDef ResolveModule(IDsDocumentService documentService, string module) {
			IDsDocument? doc;
			if (File.Exists(module))
				doc = documentService.TryGetOrCreate(DsDocumentInfo.CreateDocument(module));
			else
				doc = documentService.GetDocuments().FirstOrDefault(d =>
					string.Equals(Path.GetFileName(d.Filename), module, StringComparison.OrdinalIgnoreCase) ||
					string.Equals(d.ModuleDef?.Name, module, StringComparison.OrdinalIgnoreCase));
			return doc?.ModuleDef
				?? throw new InvalidOperationException($"could not load module '{module}'; pass a full path or open it in dnSpy first");
		}

		/// <summary>All methods named by a fully-qualified name — every overload, so a breakpoint or a decompile covers them all.</summary>
		public static MethodDef[] ResolveMethods(ModuleDef module, string fullName) {
			var idx = fullName.LastIndexOf('.');
			if (idx <= 0)
				throw new ArgumentException("must be fully qualified, e.g. 'Namespace.Type.Method'");
			var typeName = fullName.Substring(0, idx);
			var methodName = fullName.Substring(idx + 1);
			var type = FindType(module, typeName)
				?? throw new InvalidOperationException($"type not found: {typeName}");
			var methods = type.Methods.Where(m => m.Name == methodName).ToArray();
			if (methods.Length == 0)
				throw new InvalidOperationException($"method not found: {methodName} in {typeName}");
			return methods;
		}

		/// <summary>
		/// A nested type's reflection name uses '+', not '.'. The caller passes '.', so progressively
		/// turn the rightmost '.' into '+' until the type resolves (handles arbitrary nesting depth).
		/// </summary>
		public static TypeDef? FindType(ModuleDef module, string typeName) {
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
	}
}
