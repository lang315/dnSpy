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
using System.Threading;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.CallStack;
using dnSpy.Contracts.Debugger.Evaluation;
using dnSpy.Contracts.Debugger.Text;
using dnSpy.MCP.Server;

namespace dnSpy.MCP.Tools {
	/// <summary>
	/// Shared frame-resolution and expression-evaluation plumbing for the paused-debuggee tools.
	/// dbg_eval (InspectionTools) and decrypt_strings drive a func-eval exactly the same way: resolve a
	/// stack frame on the paused thread, create a language evaluation context with the right func-eval
	/// timeout, run an expression and format the result to a stable decimal string — all on
	/// <see cref="DbgManager.Dispatcher"/>. Kept in one place so the two cannot drift.
	/// </summary>
	sealed class FrameEvaluator {
		// Evaluation can run user code (property getters, ToString) on the single debug-engine thread,
		// so the 10s default in DbgAccess.Invoke is too aggressive here; give eval-based work more room.
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

		readonly DbgAccess dbg;
		readonly Lazy<DbgLanguageService> languageService;

		public FrameEvaluator(DbgAccess dbg, Lazy<DbgLanguageService> languageService) {
			this.dbg = dbg;
			this.languageService = languageService;
		}

		DbgManager Mgr => dbg.DbgManager;

		/// <summary>
		/// Resolves the frame (from frame_index/thread_id), creates a language context + eval info on the
		/// dispatcher, runs <paramref name="body"/>, and closes the context in a finally. Only the body
		/// differs between the eval-based tools, so it takes (evalInfo, language) and returns the result.
		/// </summary>
		public string WithFrame(ulong? threadId, int frameIndex, Func<DbgEvaluationInfo, DbgLanguage, string> body) {
			return dbg.Invoke(() => {
				var (frame, language) = ResolveFrame(threadId, frameIndex);
				// Deliberately NOT NoMethodBody. That option skips the decompilation this context needs:
				// measured, it leaves dbg_locals empty and every expression unresolvable. It is worth
				// stating because that decompilation is the same path dnSpy's own Locals window runs,
				// and it is where dnSpy sometimes faults — but the alternative is a tool that returns
				// nothing.
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

		/// <summary>
		/// Evaluates <paramref name="expression"/> in a resolved frame and returns the formatted value
		/// plus whether it is a thrown exception. Throws <see cref="InvalidOperationException"/> on an
		/// evaluator error. Must be called on the dispatcher, i.e. from inside a <see cref="WithFrame"/>
		/// body, where <paramref name="evalInfo"/> and <paramref name="language"/> come from.
		/// </summary>
		public (string value, bool isThrownException) Evaluate(DbgEvaluationInfo evalInfo, DbgLanguage language, string expression) {
			var ee = language.ExpressionEvaluator;
			var result = ee.Evaluate(evalInfo, expression, DbgEvaluationOptions.Expression, ee.CreateExpressionEvaluatorState());
			if (result.Error is not null)
				throw new InvalidOperationException(result.Error);
			var value = result.Value!;
			try {
				var writer = new DbgStringBuilderTextWriter();
				language.Formatter.FormatValue(evalInfo, writer, value, ValueOptions, null);
				return (writer.Text, result.IsThrownException);
			}
			finally {
				value.Close();
			}
		}

		public DbgThread ResolveThread(ulong? threadId) {
			if (threadId is null)
				return DefaultThread();
			foreach (var p in Mgr.Processes)
				foreach (var t in p.Threads)
					if (t.Id == threadId.Value)
						return t;
			throw new InvalidOperationException($"no thread with id {threadId.Value}");
		}

		/// <summary>
		/// The thread to inspect when the caller named none.
		///
		/// CurrentThread.Current is the right answer almost always, but it is not pinned to the thread
		/// that hit the breakpoint: it drifts, and a caller that lands on a runtime thread with no
		/// managed frames gets "frame index 0 out of range (0 frames)" while the process is sitting on
		/// its breakpoint. An agent has no way to tell that apart from a genuinely empty stack. Prefer
		/// the current thread when it can answer, and otherwise pick a paused thread that can.
		/// </summary>
		DbgThread DefaultThread() {
			var current = Mgr.CurrentThread.Current;
			if (current is not null && HasFrames(current))
				return current;

			foreach (var p in Mgr.Processes) {
				if (p.State != DbgProcessState.Paused)
					continue;
				foreach (var t in p.Threads) {
					if (HasFrames(t))
						return t;
				}
			}

			// Keep the original message: no thread could answer, which is what the caller needs to know.
			return current ?? throw new InvalidOperationException("no current thread; is a process paused?");
		}

		static bool HasFrames(DbgThread thread) {
			try {
				return thread.Process.State == DbgProcessState.Paused && thread.GetFrames(1).Length > 0;
			}
			catch (Exception) {
				// A thread that faults while being asked is not one to hand back.
				return false;
			}
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
