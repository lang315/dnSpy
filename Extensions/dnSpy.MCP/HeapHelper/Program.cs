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
using System.Text.Json.Nodes;
using Microsoft.Diagnostics.Runtime;

namespace dnSpy.MCP.HeapHelper {
	/// <summary>
	/// Out-of-process managed-heap walker for the dnSpy.MCP heap tools (heap_stats / heap_find /
	/// heap_object). It exists because dnSpy's engine is built against ClrMD 1.1, which cannot walk a
	/// .NET 6+ "regions" GC heap (it reports only a handful of system types and zero user objects),
	/// while ClrMD 4.x walks it fully. The extension cannot reference 4.x in-process (the bin dir ships
	/// one <c>Microsoft.Diagnostics.Runtime.dll</c> and the engine is bound to 1.1), so the walk is
	/// isolated here in a tiny helper process that references ClrMD 4.x. ClrMD reads the debuggee's DAC
	/// in-process, so this helper's bitness must match the debuggee's — the extension launches the
	/// win-x64 or win-x86 build accordingly.
	///
	/// Contract: the extension spawns this with one of
	///   stats  &lt;pid&gt; [top]
	///   find   &lt;pid&gt; &lt;typeName&gt; [max]     (typeName may end with '*' for a prefix match)
	///   object &lt;pid&gt; &lt;address&gt;            (address as 0x-hex or decimal)
	/// and reads a single JSON object from stdout. Anything other than the result JSON (there is
	/// nothing else here) would corrupt the pipe, so failures are reported as {"error":"..."} on
	/// stdout too; diagnostics, if any, go to stderr. The JSON field names mirror exactly what the
	/// in-process ClrMD 1.1 tools used to return, so the extension passes them straight through.
	/// </summary>
	static class Program {
		// Upper bound on objects examined in a single walk, so a pathological heap cannot hang it.
		const int MaxObjectsScanned = 2_000_000;
		// Field-summary caps: small for the per-instance view in find, generous for a single object.
		const int MaxFindFields = 12;
		const int MaxObjectFields = 200;
		// Array-element cap for object, so a huge array is previewed, not dumped whole.
		const int MaxArrayElements = 256;
		// Cap on the characters read from a string object/field.
		const int MaxStringLength = 64 * 1024;

		static int Main(string[] args) {
			try {
				var result = Run(args);
				// Only ever the result JSON on stdout — the parent reads stdout to completion as one object.
				Console.Out.Write(result.ToJsonString());
				return 0;
			}
			catch (Exception ex) {
				// A torn-down/exited process, a bitness mismatch, no CLR, a bad argument, a DAC surprise:
				// all collapse to a single error object the extension turns into a clean tool error.
				var err = new JsonObject { ["error"] = ex.Message };
				try { Console.Out.Write(err.ToJsonString()); }
				catch { /* stdout gone: nothing more we can do */ }
				return 1;
			}
		}

		static JsonObject Run(string[] args) {
			if (args.Length < 2)
				throw new ArgumentException("usage: stats <pid> [top] | find <pid> <type> [max] | object <pid> <address>");
			var cmd = args[0];
			var pid = ParseInt(args[1], "pid");
			switch (cmd) {
			case "stats": {
				var top = args.Length >= 3 ? ParseInt(args[2], "top") : 100;
				return WithHeap(pid, (heap, ptr) => Stats(heap, top));
			}
			case "find": {
				if (args.Length < 3)
					throw new ArgumentException("find requires a type name");
				var type = args[2];
				var max = args.Length >= 4 ? ParseInt(args[3], "max") : 200;
				return WithHeap(pid, (heap, ptr) => Find(heap, ptr, type, max));
			}
			case "object": {
				if (args.Length < 3)
					throw new ArgumentException("object requires an address");
				var addr = ParseAddress(args[2]);
				return WithHeap(pid, (heap, ptr) => ObjectAt(heap, ptr, addr));
			}
			default:
				throw new ArgumentException("unknown command: " + cmd);
			}
		}

		// Attach ClrMD 4.x to the (paused) debuggee, hand its heap + pointer size to <paramref name="body"/>,
		// and always dispose the DataTarget. suspend:false is the passive-equivalent — it reads memory and an
		// in-process DAC without using the debugger APIs, so it coexists with dnSpy's live ICorDebug attach and
		// never suspends the target. The process is frozen by CorDebug at a breakpoint, so the heap is static.
		static JsonObject WithHeap(int pid, Func<ClrHeap, int, JsonObject> body) {
			using var dt = DataTarget.AttachToProcess(pid, false);
			if (dt.ClrVersions.Length == 0)
				throw new InvalidOperationException($"no managed CLR found in pid {pid}; heap inspection needs a .NET Framework or .NET/CoreCLR process (Mono/Unity is not supported)");
			var runtime = dt.ClrVersions[0].CreateRuntime();
			var heap = runtime.Heap;
			if (!heap.CanWalkHeap)
				throw new InvalidOperationException("the managed heap is not currently walkable (GC structures are mid-update); resume and pause again, then retry");
			return body(heap, dt.DataReader.PointerSize);
		}

		static JsonObject Stats(ClrHeap heap, int top) {
			var byType = new Dictionary<string, TypeStat>(StringComparer.Ordinal);
			long totalObjects = 0;
			ulong totalBytes = 0;
			var truncated = false;
			var scanned = 0;
			foreach (var obj in heap.EnumerateObjects()) {
				if (scanned >= MaxObjectsScanned) {
					truncated = true;
					break;
				}
				scanned++;
				var t = obj.Type;
				// Skip nulls and the "Free" pseudo-objects (GC gaps) so totals count real objects.
				if (t is null || obj.IsFree)
					continue;
				var size = obj.Size;
				var name = t.Name ?? "<unknown>";
				totalObjects++;
				totalBytes += size;
				if (byType.TryGetValue(name, out var cur))
					byType[name] = new TypeStat(cur.Count + 1, cur.Bytes + size);
				else
					byType[name] = new TypeStat(1, size);
			}
			var typesArr = new JsonArray();
			foreach (var kv in byType.OrderByDescending(k => k.Value.Bytes).Take(top)) {
				typesArr.Add(new JsonObject {
					["type"] = kv.Key,
					["count"] = kv.Value.Count,
					["totalBytes"] = kv.Value.Bytes,
				});
			}
			return new JsonObject {
				["totalObjects"] = totalObjects,
				["totalBytes"] = totalBytes,
				["typeCount"] = byType.Count,
				["truncated"] = truncated,
				["types"] = typesArr,
			};
		}

		static JsonObject Find(ClrHeap heap, int pointerSize, string typeArg, int max) {
			// A trailing '*' turns the exact-name match into a prefix match.
			var wildcard = typeArg.EndsWith("*", StringComparison.Ordinal);
			var needle = wildcard ? typeArg.Substring(0, typeArg.Length - 1) : typeArg;
			long count = 0;
			var truncated = false;
			var scanned = 0;
			var instances = new JsonArray();
			foreach (var obj in heap.EnumerateObjects()) {
				if (scanned >= MaxObjectsScanned) {
					truncated = true;
					break;
				}
				scanned++;
				var t = obj.Type;
				if (t is null || obj.IsFree)
					continue;
				var name = t.Name;
				if (name is null)
					continue;
				var match = wildcard
					? name.StartsWith(needle, StringComparison.Ordinal)
					: name == needle;
				if (!match)
					continue;
				count++;
				// Detail only the first 'max'; keep counting so 'count' is the true total.
				if (instances.Count < max) {
					instances.Add(new JsonObject {
						["address"] = Hex(obj.Address),
						["type"] = name,
						["size"] = obj.Size,
						["fields"] = FieldsSummary(obj, t, pointerSize, MaxFindFields),
					});
				}
			}
			// More instances exist than were detailed => the instance list is truncated.
			if (count > instances.Count)
				truncated = true;
			return new JsonObject {
				["type"] = typeArg,
				["count"] = count,
				["truncated"] = truncated,
				["instances"] = instances,
			};
		}

		static JsonObject ObjectAt(ClrHeap heap, int pointerSize, ulong addr) {
			var obj = heap.GetObject(addr);
			var t = obj.Type;
			if (t is null)
				throw new InvalidOperationException($"no managed object at 0x{addr:X} (the address is not the start of a live heap object)");
			var res = new JsonObject {
				["address"] = Hex(addr),
				["type"] = t.Name,
				["size"] = obj.Size,
				["isArray"] = t.IsArray,
			};
			if (t.IsString)
				res["stringValue"] = SafeString(obj);
			if (t.IsArray) {
				var arr = obj.AsArray();
				var len = arr.Length;
				res["arrayLength"] = len;
				// The element kind drives formatting: primitives inline, references as an address.
				var et = t.ComponentType?.ElementType ?? ClrElementType.Unknown;
				var take = Math.Min(len, MaxArrayElements);
				var elements = new JsonArray();
				for (int i = 0; i < take; i++)
					elements.Add(ReadArrayElement(arr, et, i, pointerSize));
				res["elements"] = elements;
				if (take < len)
					res["elementsTruncated"] = true;
			}
			res["fields"] = FieldsSummary(obj, t, pointerSize, MaxObjectFields);
			return res;
		}

		// Summarise an object's fields as {name: value}. Only "simple value" fields are included (scalars,
		// strings and references — the latter as an address); embedded struct fields have no simple value
		// and are skipped. Each field read is guarded so one unreadable field cannot fail the whole summary.
		static JsonObject FieldsSummary(ClrObject obj, ClrType type, int pointerSize, int maxFields) {
			var res = new JsonObject();
			var n = 0;
			foreach (var f in type.Fields) {
				if (n >= maxFields)
					break;
				if (!TryFormatField(obj, f, pointerSize, out var node))
					continue;
				var key = f.Name ?? ("<field" + n + ">");
				// A duplicated name (shadowed base field) must not throw on re-add; last one wins.
				res[key] = node;
				n++;
			}
			return res;
		}

		// Read one field's simple value, mirroring ClrMD 1.1's HasSimpleValue semantics: strings verbatim,
		// object references and pointers as a hex address, primitives as their JSON value, structs skipped
		// (returns false). Returns false — a skip — on any read failure so the field is simply omitted.
		static bool TryFormatField(ClrObject obj, ClrInstanceField f, int pointerSize, out JsonNode? node) {
			node = null;
			var name = f.Name;
			if (name is null)
				return false;
			var et = f.ElementType;
			try {
				if (et == ClrElementType.String) {
					node = obj.ReadStringField(name, MaxStringLength);
					return true;
				}
				if (et is ClrElementType.Pointer or ClrElementType.FunctionPointer or ClrElementType.NativeInt or ClrElementType.NativeUInt) {
					var p = pointerSize == 4 ? obj.ReadField<uint>(name) : obj.ReadField<ulong>(name);
					node = Hex(p);
					return true;
				}
				if (f.IsObjectReference) {
					var r = obj.ReadObjectField(name);
					node = Hex(r.Address);
					return true;
				}
				if (f.IsPrimitive) {
					node = PrimitiveNode(ReadFieldPrimitive(obj, f, name));
					return true;
				}
				// Embedded struct / value type: no simple value.
				return false;
			}
			catch {
				return false;
			}
		}

		static object? ReadFieldPrimitive(ClrObject obj, ClrInstanceField f, string name) {
			switch (f.ElementType) {
			case ClrElementType.Boolean: return obj.ReadField<bool>(name);
			case ClrElementType.Char: return obj.ReadField<char>(name);
			case ClrElementType.Int8: return obj.ReadField<sbyte>(name);
			case ClrElementType.UInt8: return obj.ReadField<byte>(name);
			case ClrElementType.Int16: return obj.ReadField<short>(name);
			case ClrElementType.UInt16: return obj.ReadField<ushort>(name);
			case ClrElementType.Int32: return obj.ReadField<int>(name);
			case ClrElementType.UInt32: return obj.ReadField<uint>(name);
			case ClrElementType.Int64: return obj.ReadField<long>(name);
			case ClrElementType.UInt64: return obj.ReadField<ulong>(name);
			case ClrElementType.Float: return obj.ReadField<float>(name);
			case ClrElementType.Double: return obj.ReadField<double>(name);
			default: return null;
			}
		}

		// Format one array element by the array's component kind, matching the field formatting above.
		static JsonNode? ReadArrayElement(ClrArray arr, ClrElementType et, int i, int pointerSize) {
			try {
				if (et == ClrElementType.String)
					return arr.GetObjectValue(i).AsString(MaxStringLength);
				if (et is ClrElementType.Pointer or ClrElementType.FunctionPointer or ClrElementType.NativeInt or ClrElementType.NativeUInt) {
					var p = pointerSize == 4 ? (ulong)arr.GetValue<uint>(i) : arr.GetValue<ulong>(i);
					return Hex(p);
				}
				if (IsReference(et))
					return Hex(arr.GetObjectValue(i).Address);
				return PrimitiveNode(ReadArrayPrimitive(arr, et, i));
			}
			catch {
				return null;
			}
		}

		static object? ReadArrayPrimitive(ClrArray arr, ClrElementType et, int i) {
			switch (et) {
			case ClrElementType.Boolean: return arr.GetValue<bool>(i);
			case ClrElementType.Char: return arr.GetValue<char>(i);
			case ClrElementType.Int8: return arr.GetValue<sbyte>(i);
			case ClrElementType.UInt8: return arr.GetValue<byte>(i);
			case ClrElementType.Int16: return arr.GetValue<short>(i);
			case ClrElementType.UInt16: return arr.GetValue<ushort>(i);
			case ClrElementType.Int32: return arr.GetValue<int>(i);
			case ClrElementType.UInt32: return arr.GetValue<uint>(i);
			case ClrElementType.Int64: return arr.GetValue<long>(i);
			case ClrElementType.UInt64: return arr.GetValue<ulong>(i);
			case ClrElementType.Float: return arr.GetValue<float>(i);
			case ClrElementType.Double: return arr.GetValue<double>(i);
			default: return null;
			}
		}

		static bool IsReference(ClrElementType et) =>
			et is ClrElementType.Class or ClrElementType.Object or ClrElementType.Array or ClrElementType.SZArray;

		// Map a boxed primitive to a JSON value without letting the serializer guess. ulong is kept as-is
		// (it can exceed long); every other integer widens to long; float/double to double; char to string.
		static JsonNode? PrimitiveNode(object? v) {
			switch (v) {
			case null:
				return null;
			case bool b:
				return JsonValue.Create(b);
			case char c:
				return JsonValue.Create(c.ToString());
			case ulong ul:
				return JsonValue.Create(ul);
			case byte or sbyte or short or ushort or int or uint or long:
				return JsonValue.Create(Convert.ToInt64(v, CultureInfo.InvariantCulture));
			case float or double:
				return JsonValue.Create(Convert.ToDouble(v, CultureInfo.InvariantCulture));
			default:
				return JsonValue.Create(v.ToString());
			}
		}

		static JsonNode? SafeString(ClrObject obj) {
			try {
				return obj.AsString(MaxStringLength);
			}
			catch {
				return null;
			}
		}

		static int ParseInt(string s, string field) {
			if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
				return v;
			throw new ArgumentException($"'{field}' is not a valid integer: {s}");
		}

		static ulong ParseAddress(string s) {
			s = s.Trim();
			var isHex = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
			var body = isHex ? s.Substring(2) : s;
			if (ulong.TryParse(body, isHex ? NumberStyles.HexNumber : NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
				return v;
			throw new ArgumentException($"'address' is not a valid number: {s}");
		}

		static string Hex(ulong value) => "0x" + value.ToString("X");

		readonly struct TypeStat {
			public readonly long Count;
			public readonly ulong Bytes;
			public TypeStat(long count, ulong bytes) {
				Count = count;
				Bytes = bytes;
			}
		}
	}
}
