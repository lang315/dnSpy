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
using System.Threading;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.CallStack;
using dnSpy.Contracts.Debugger.Code;
using dnSpy.Contracts.Debugger.DotNet.Code;
using dnSpy.Contracts.Debugger.Evaluation;
using dnSpy.Contracts.Debugger.Text;
using dnSpy.MCP.Server;
using Newtonsoft.Json.Linq;
using static dnSpy.MCP.Tools.JsonUtils;

namespace dnSpy.MCP.Tools {
	/// <summary>Read-only inspection of a paused debuggee: threads, call stack, locals, modules, eval.</summary>
	sealed class InspectionTools {
		// Evaluation can run user code (property getters, ToString) on the single debug-engine thread,
		// so the 10s default in DbgAccess.Invoke is too aggressive here; give eval-based tools more room.
		const int EvalTimeoutMs = 60000;

		// dnSpy's own func-eval cap (DbgLanguage.DefaultFuncEvalTimeout) is one second, which is far
		// too short for an agent calling a real method — it reports "Evaluation timed out" long before
		// the marshalling timeout above matters. Cap below EvalTimeoutMs so a slow evaluation fails
		// with the engine's own message rather than as a dispatcher timeout.
		static readonly TimeSpan FuncEvalTimeout = TimeSpan.FromSeconds(30);

		// Format numbers in decimal. Without this the output follows dnSpy's UI "hexadecimal display"
		// toggle, so the same expression answers 7 or 0x00000007 depending on a setting the agent
		// cannot see. A machine consumer needs one stable representation.
		const DbgValueFormatterOptions ValueOptions = DbgValueFormatterOptions.Decimal;

		// Enumerating an object's members needs more than the default. With no options at all every
		// child of a class instance comes back as "Internal debugger error" — arrays still expand,
		// because their children are plain element accesses, which is what made the gap easy to miss.
		const DbgValueNodeEvaluationOptions NodeOptions =
			DbgValueNodeEvaluationOptions.PublicMembers |
			DbgValueNodeEvaluationOptions.HideCompilerGeneratedMembers |
			DbgValueNodeEvaluationOptions.RespectHideMemberAttributes;

		const DbgStackFrameFormatterOptions FrameOptions =
			DbgStackFrameFormatterOptions.ModuleNames |
			DbgStackFrameFormatterOptions.ParameterTypes |
			DbgStackFrameFormatterOptions.ParameterNames |
			DbgStackFrameFormatterOptions.DeclaringTypes |
			DbgStackFrameFormatterOptions.ReturnTypes |
			DbgStackFrameFormatterOptions.Namespaces |
			DbgStackFrameFormatterOptions.IntrinsicTypeKeywords;

		readonly DbgAccess dbg;
		readonly Lazy<DbgLanguageService> languageService;
		readonly Lazy<DbgDotNetCodeLocationFactory> codeLocationFactory;

		public InspectionTools(DbgAccess dbg, Lazy<DbgLanguageService> languageService,
			Lazy<DbgDotNetCodeLocationFactory> codeLocationFactory) {
			this.dbg = dbg;
			this.languageService = languageService;
			this.codeLocationFactory = codeLocationFactory;
		}

		DbgManager Mgr => dbg.DbgManager;

		public IEnumerable<ToolDef> Create() {
			yield return new ToolDef("dbg_threads",
				"List the threads of the debugged process(es).",
				Schema.Object(),
				_ => Threads(), readOnly: true);

			yield return new ToolDef("dbg_modules",
				"List the loaded modules (assemblies) of the debugged process(es).",
				Schema.Object(),
				_ => Modules(), readOnly: true);

			yield return new ToolDef("dbg_callstack",
				"Get the call stack of the paused thread (or a specific thread by id).",
				Schema.Object(
					("thread_id", Schema.Int("Native thread id; defaults to the current thread"), false),
					("max_frames", Schema.Int("Maximum frames to return (default 200)"), false)),
				CallStack, readOnly: true);

			yield return new ToolDef("dbg_locals",
				"Get the local variables and parameters of a stack frame on the paused thread.",
				Schema.Object(
					("frame_index", Schema.Int("Frame index, 0 = top (default 0)"), false),
					("thread_id", Schema.Int("Native thread id; defaults to the current thread"), false)),
				Locals, readOnly: true);

			yield return new ToolDef("dbg_eval",
				"Evaluate a C#/VB expression in the context of a stack frame on the paused thread.",
				Schema.Object(
					("expression", Schema.Str("Expression to evaluate"), true),
					("frame_index", Schema.Int("Frame index, 0 = top (default 0)"), false),
					("thread_id", Schema.Int("Native thread id; defaults to the current thread"), false)),
				Eval);

			yield return new ToolDef("dbg_expand",
				"Evaluate an expression and list its immediate child members (fields/elements). Use to drill into objects and arrays.",
				Schema.Object(
					("expression", Schema.Str("Expression whose children to list"), true),
					("frame_index", Schema.Int("Frame index, 0 = top (default 0)"), false),
					("max_children", Schema.Int("Maximum children to return (default 100)"), false),
					("thread_id", Schema.Int("Native thread id; defaults to the current thread"), false)),
				Expand);

			yield return new ToolDef("dbg_variables",
				"List a category of variables on the paused thread: returns (return values), statics (static fields), or exceptions. " +
				"autos is not implemented by dnSpy's .NET engine and always fails — use dbg_locals instead.",
				Schema.Object(
					("kind", Schema.Str("returns | statics | exceptions (autos is unsupported on the .NET engine)"), true),
					("frame_index", Schema.Int("Frame index, 0 = top (default 0)"), false),
					("thread_id", Schema.Int("Native thread id; defaults to the current thread"), false)),
				Variables);

			yield return new ToolDef("dbg_set_variable",
				"Assign a new value to a variable/expression in a stack frame (C#/VB assignment).",
				Schema.Object(
					("target", Schema.Str("Left-hand side, e.g. 'this.count' or 'x'"), true),
					("value", Schema.Str("Right-hand side expression"), true),
					("frame_index", Schema.Int("Frame index, 0 = top (default 0)"), false),
					("thread_id", Schema.Int("Native thread id; defaults to the current thread"), false)),
				SetVariable, destructive: true);

			yield return new ToolDef("dbg_set_next_statement",
				"Move the instruction pointer of the paused top frame to a different IL offset in the same method.",
				Schema.Object(
					("il_offset", Schema.Str("Target IL offset, hex or decimal"), true),
					("thread_id", Schema.Int("Native thread id; defaults to the current thread"), false)),
				SetNextStatement, destructive: true);
		}

		string Threads() => dbg.Invoke(() => {
			var arr = new JArray();
			foreach (var p in Mgr.Processes)
				foreach (var t in p.Threads)
					arr.Add(ThreadJson(t));
			return Json(arr);
		});

		string Modules() => dbg.Invoke(() => {
			var arr = new JArray();
			foreach (var p in Mgr.Processes)
				foreach (var r in p.Runtimes)
					foreach (var m in r.Modules)
						arr.Add(Module(m));
			return Json(arr);
		});

		string CallStack(JObject args) {
			var threadId = (ulong?)(long?)args["thread_id"];
			var maxFrames = Clamp((int?)args["max_frames"] ?? 200, 1, 1000);
			return dbg.Invoke(() => {
				var thread = ResolveThread(threadId);
				var language = languageService.Value.GetCurrentLanguage(thread.Runtime.RuntimeKindGuid);
				var frames = thread.GetFrames(maxFrames);
				var writer = new DbgStringBuilderTextWriter();
				var arr = new JArray();
				for (int i = 0; i < frames.Length; i++) {
					var frame = frames[i];
					var context = language.CreateContext(frame, options: DbgEvaluationContextOptions.NoMethodBody, cancellationToken: CancellationToken.None);
					try {
						var evalInfo = new DbgEvaluationInfo(context, frame, CancellationToken.None);
						writer.Reset();
						language.Formatter.FormatFrame(evalInfo, writer, FrameOptions, ValueOptions, null);
						arr.Add(new JObject {
							["index"] = i,
							["frame"] = writer.Text,
							["module"] = frame.Module?.Name,
							["token"] = frame.HasFunctionToken ? "0x" + frame.FunctionToken.ToString("X8") : null,
							["offset"] = "0x" + frame.FunctionOffset.ToString("X"),
						});
					}
					finally {
						context.Close();
					}
				}
				return Json(new JObject {
					["threadId"] = (long)thread.Id,
					["frames"] = arr,
				});
			}, EvalTimeoutMs);
		}

		// Every eval-based tool resolves the frame (from frame_index/thread_id), creates a language
		// context + eval info on the dispatcher, runs its body, and closes the context in a finally.
		// Only the body differs, so it takes (evalInfo, language) and returns the text result.
		string WithFrame(JObject args, Func<DbgEvaluationInfo, DbgLanguage, string> body) {
			var frameIndex = (int?)args["frame_index"] ?? 0;
			var threadId = (ulong?)(long?)args["thread_id"];
			return dbg.Invoke(() => {
				var (frame, language) = ResolveFrame(threadId, frameIndex);
				var context = language.CreateContext(frame, funcEvalTimeout: FuncEvalTimeout,
					cancellationToken: CancellationToken.None);
				try {
					var evalInfo = new DbgEvaluationInfo(context, frame, CancellationToken.None);
					return body(evalInfo, language);
				}
				finally {
					context.Close();
				}
			}, EvalTimeoutMs);
		}

		string Locals(JObject args) => WithFrame(args, (evalInfo, language) => {
			var nodes = language.LocalsProvider.GetNodes(evalInfo, DbgValueNodeEvaluationOptions.None, DbgLocalsValueNodeEvaluationOptions.None);
			var writer = new DbgStringBuilderTextWriter();
			var arr = new JArray();
			foreach (var info in nodes) {
				var node = info.ValueNode;
				arr.Add(new JObject {
					["kind"] = info.Kind.ToString(),
					["name"] = FormatName(node, evalInfo, writer),
					["value"] = node.HasError ? node.ErrorMessage : FormatValue(node, evalInfo, writer),
				});
			}
			return Json(arr);
		});

		string Eval(JObject args) {
			var expression = (string?)args["expression"] ?? throw new ArgumentException("'expression' is required");
			return WithFrame(args, (evalInfo, language) => {
				var ee = language.ExpressionEvaluator;
				var result = ee.Evaluate(evalInfo, expression, DbgEvaluationOptions.Expression, ee.CreateExpressionEvaluatorState());
				if (result.Error is not null)
					throw new InvalidOperationException(result.Error);
				var value = result.Value!;
				try {
					var writer = new DbgStringBuilderTextWriter();
					language.Formatter.FormatValue(evalInfo, writer, value, ValueOptions, null);
					return Json(new JObject {
						["expression"] = expression,
						["value"] = writer.Text,
						["isThrownException"] = result.IsThrownException,
					});
				}
				finally {
					value.Close();
				}
			});
		}

		string Expand(JObject args) {
			var expression = (string?)args["expression"] ?? throw new ArgumentException("'expression' is required");
			var maxChildren = Clamp((int?)args["max_children"] ?? 100, 1, 1000);
			return WithFrame(args, (evalInfo, language) => {
				var ee = language.ExpressionEvaluator;
				var res = language.ValueNodeFactory.Create(evalInfo, expression,
					NodeOptions, DbgEvaluationOptions.Expression, ee.CreateExpressionEvaluatorState());
				var node = res.ValueNode;
				var writer = new DbgStringBuilderTextWriter();
				var obj = new JObject {
					["expression"] = expression,
					["value"] = node.HasError ? node.ErrorMessage : FormatValue(node, evalInfo, writer),
					["hasChildren"] = node.HasChildren == true,
				};
				if (node.HasChildren == true) {
					var count = node.GetChildCount(evalInfo);
					var take = (int)Math.Min((ulong)maxChildren, count);
					var arr = new JArray();
					foreach (var child in node.GetChildren(evalInfo, 0, take, NodeOptions)) {
						arr.Add(new JObject {
							["name"] = FormatName(child, evalInfo, writer),
							["value"] = child.HasError ? child.ErrorMessage : FormatValue(child, evalInfo, writer),
						});
					}
					obj["childCount"] = (long)count;
					obj["children"] = arr;
				}
				return Json(obj);
			});
		}

		string Variables(JObject args) {
			var kind = ((string?)args["kind"] ?? throw new ArgumentException("'kind' is required")).ToLowerInvariant();
			return WithFrame(args, (evalInfo, language) => {
				DbgValueNodeProvider provider = kind switch {
					"autos" => language.AutosProvider,
					"returns" => language.ReturnValuesProvider,
					"statics" => language.StaticFieldsProvider,
					"exceptions" => language.ExceptionsProvider,
					_ => throw new ArgumentException("'kind' must be autos, returns, statics, or exceptions"),
				};
				var writer = new DbgStringBuilderTextWriter();
				var nodes = provider.GetNodes(evalInfo, DbgValueNodeEvaluationOptions.None);
				if (kind == "autos" && IsNotImplementedStub(nodes, evalInfo, writer))
					throw new InvalidOperationException("autos is not implemented by dnSpy's .NET engine; use dbg_locals instead");
				var arr = new JArray();
				foreach (var node in nodes) {
					arr.Add(new JObject {
						["name"] = FormatName(node, evalInfo, writer),
						["value"] = node.HasError ? node.ErrorMessage : FormatValue(node, evalInfo, writer),
					});
				}
				return Json(arr);
			});
		}

		// dnSpy's .NET autos provider (DbgEngineAutosProviderImpl) is a stub: instead of failing it hands
		// back one error node named "Error" carrying the message "NYI", which serialises as a plausible
		// single-entry variable list and leads a caller to reason from a non-answer. Matching on that
		// sentinel is brittle, so demand the whole shape — exactly one node, it is an error node, and both
		// strings match — and never mistake a real variable for it. The day dnSpy implements the provider
		// this stops matching and autos simply works again; the integration test guarding it fails then,
		// which is the signal to delete this check.
		static bool IsNotImplementedStub(Contracts.Debugger.Evaluation.DbgValueNode[] nodes, DbgEvaluationInfo evalInfo, DbgStringBuilderTextWriter writer) =>
			nodes.Length == 1 && nodes[0].HasError && nodes[0].ErrorMessage == "NYI" &&
			FormatName(nodes[0], evalInfo, writer) == "Error";

		string SetVariable(JObject args) {
			var target = (string?)args["target"] ?? throw new ArgumentException("'target' is required");
			var value = (string?)args["value"] ?? throw new ArgumentException("'value' is required");
			return WithFrame(args, (evalInfo, language) => {
				var result = language.ExpressionEvaluator.Assign(evalInfo, target, value, DbgEvaluationOptions.Expression);
				if (result.Error is not null)
					throw new InvalidOperationException(result.Error);
				return $"{target} = {value}";
			});
		}

		string SetNextStatement(JObject args) {
			var offset = ParseUInt((string?)args["il_offset"], "il_offset");
			var threadId = (ulong?)(long?)args["thread_id"];
			return dbg.Invoke(() => {
				var thread = ResolveThread(threadId);
				if (thread.Process.State != DbgProcessState.Paused)
					throw new InvalidOperationException("thread's process is not paused");
				var frame = thread.GetTopStackFrame()
					?? throw new InvalidOperationException("no current stack frame");
				// A JIT-compiled frame reports a native location (DbgDotNetNativeCodeLocation); both it and the
				// IL location implement IDbgDotNetCodeLocation, so match the interface, not the concrete class.
				if (frame.Location is not IDbgDotNetCodeLocation loc)
					throw new InvalidOperationException("current frame has no .NET code location");
				var newLoc = codeLocationFactory.Value.Create(loc.Module, loc.Token, offset);
				// We own newLoc; let the runtime close it on the next continue (SetIP doesn't take ownership).
				thread.Runtime.CloseOnContinue(newLoc);
				if (!thread.CanSetIP(newLoc))
					throw new InvalidOperationException("cannot set next statement to that IL offset");
				thread.SetIP(newLoc);
				// SetIP is dispatched asynchronously; CanSetIP passed, but the move completes shortly after.
				return $"requested move of instruction pointer to IL offset 0x{offset:X}";
			});
		}

		static string FormatName(Contracts.Debugger.Evaluation.DbgValueNode node, DbgEvaluationInfo evalInfo, DbgStringBuilderTextWriter writer) {
			writer.Reset();
			node.FormatName(evalInfo, writer, DbgValueFormatterOptions.None);
			return writer.Text;
		}

		static string FormatValue(Contracts.Debugger.Evaluation.DbgValueNode node, DbgEvaluationInfo evalInfo, DbgStringBuilderTextWriter writer) {
			writer.Reset();
			node.FormatValue(evalInfo, writer, ValueOptions, null);
			return writer.Text;
		}

		DbgThread ResolveThread(ulong? threadId) {
			if (threadId is null) {
				return Mgr.CurrentThread.Current
					?? throw new InvalidOperationException("no current thread; is a process paused?");
			}
			foreach (var p in Mgr.Processes)
				foreach (var t in p.Threads)
					if (t.Id == threadId.Value)
						return t;
			throw new InvalidOperationException($"no thread with id {threadId.Value}");
		}

		(DbgStackFrame frame, DbgLanguage language) ResolveFrame(ulong? threadId, int frameIndex) {
			if (frameIndex < 0)
				throw new ArgumentException("'frame_index' must be >= 0");
			var thread = ResolveThread(threadId);
			if (thread.Process.State != DbgProcessState.Paused)
				throw new InvalidOperationException("thread's process is not paused");
			var frames = thread.GetFrames(frameIndex + 1);
			if (frameIndex >= frames.Length)
				throw new InvalidOperationException($"frame index {frameIndex} out of range ({frames.Length} frames)");
			var language = languageService.Value.GetCurrentLanguage(thread.Runtime.RuntimeKindGuid);
			return (frames[frameIndex], language);
		}
	}
}
