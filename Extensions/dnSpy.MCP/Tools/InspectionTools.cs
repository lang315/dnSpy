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
using System.Threading;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.CallStack;
using dnSpy.Contracts.Debugger.Evaluation;
using dnSpy.Contracts.Debugger.Text;
using dnSpy.MCP.Server;
using Newtonsoft.Json.Linq;
using static dnSpy.MCP.Tools.JsonUtils;

namespace dnSpy.MCP.Tools {
	/// <summary>Read-only inspection of a paused debuggee: threads, call stack, locals, modules, eval.</summary>
	sealed class InspectionTools {
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

		public InspectionTools(DbgAccess dbg, Lazy<DbgLanguageService> languageService) {
			this.dbg = dbg;
			this.languageService = languageService;
		}

		DbgManager Mgr => dbg.DbgManager;

		public IEnumerable<ToolDef> Create() {
			yield return new ToolDef("dbg_threads",
				"List the threads of the debugged process(es).",
				Schema.Object(),
				_ => Threads());

			yield return new ToolDef("dbg_modules",
				"List the loaded modules (assemblies) of the debugged process(es).",
				Schema.Object(),
				_ => Modules());

			yield return new ToolDef("dbg_callstack",
				"Get the call stack of the paused thread (or a specific thread by id).",
				Schema.Object(
					("thread_id", Schema.Int("Native thread id; defaults to the current thread"), false),
					("max_frames", Schema.Int("Maximum frames to return (default 200)"), false)),
				CallStack);

			yield return new ToolDef("dbg_locals",
				"Get the local variables and parameters of a stack frame on the paused thread.",
				Schema.Object(
					("frame_index", Schema.Int("Frame index, 0 = top (default 0)"), false),
					("thread_id", Schema.Int("Native thread id; defaults to the current thread"), false)),
				Locals);

			yield return new ToolDef("dbg_eval",
				"Evaluate a C#/VB expression in the context of a stack frame on the paused thread.",
				Schema.Object(
					("expression", Schema.Str("Expression to evaluate"), true),
					("frame_index", Schema.Int("Frame index, 0 = top (default 0)"), false),
					("thread_id", Schema.Int("Native thread id; defaults to the current thread"), false)),
				Eval);
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
			var maxFramesRaw = (int?)args["max_frames"] ?? 200;
			var maxFrames = maxFramesRaw < 1 ? 1 : maxFramesRaw > 1000 ? 1000 : maxFramesRaw;
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
						language.Formatter.FormatFrame(evalInfo, writer, FrameOptions, DbgValueFormatterOptions.None, null);
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
			});
		}

		string Locals(JObject args) {
			var frameIndex = (int?)args["frame_index"] ?? 0;
			var threadId = (ulong?)(long?)args["thread_id"];
			return dbg.Invoke(() => {
				var (frame, language) = ResolveFrame(threadId, frameIndex);
				var context = language.CreateContext(frame, cancellationToken: CancellationToken.None);
				try {
					var evalInfo = new DbgEvaluationInfo(context, frame, CancellationToken.None);
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
				}
				finally {
					context.Close();
				}
			});
		}

		string Eval(JObject args) {
			var expression = (string?)args["expression"] ?? throw new ArgumentException("'expression' is required");
			var frameIndex = (int?)args["frame_index"] ?? 0;
			var threadId = (ulong?)(long?)args["thread_id"];
			return dbg.Invoke(() => {
				var (frame, language) = ResolveFrame(threadId, frameIndex);
				var context = language.CreateContext(frame, cancellationToken: CancellationToken.None);
				try {
					var evalInfo = new DbgEvaluationInfo(context, frame, CancellationToken.None);
					var ee = language.ExpressionEvaluator;
					var result = ee.Evaluate(evalInfo, expression, DbgEvaluationOptions.Expression, ee.CreateExpressionEvaluatorState());
					if (result.Error is not null)
						throw new InvalidOperationException(result.Error);
					var value = result.Value!;
					try {
						var writer = new DbgStringBuilderTextWriter();
						language.Formatter.FormatValue(evalInfo, writer, value, DbgValueFormatterOptions.None, null);
						return Json(new JObject {
							["expression"] = expression,
							["value"] = writer.Text,
							["isThrownException"] = result.IsThrownException,
						});
					}
					finally {
						value.Close();
					}
				}
				finally {
					context.Close();
				}
			});
		}

		static string FormatName(Contracts.Debugger.Evaluation.DbgValueNode node, DbgEvaluationInfo evalInfo, DbgStringBuilderTextWriter writer) {
			writer.Reset();
			node.FormatName(evalInfo, writer, DbgValueFormatterOptions.None);
			return writer.Text;
		}

		static string FormatValue(Contracts.Debugger.Evaluation.DbgValueNode node, DbgEvaluationInfo evalInfo, DbgStringBuilderTextWriter writer) {
			writer.Reset();
			node.FormatValue(evalInfo, writer, DbgValueFormatterOptions.None, null);
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
