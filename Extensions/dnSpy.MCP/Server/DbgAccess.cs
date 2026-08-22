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
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using dnSpy.Contracts.Debugger;

namespace dnSpy.MCP.Server {
	/// <summary>
	/// Marshals delegates onto the debugger engine thread (<see cref="DbgManager.Dispatcher"/>).
	/// All debugger objects (processes, threads, frames, value nodes) must be touched on that
	/// thread; <see cref="DbgDispatcher"/> only offers fire-and-forget <c>BeginInvoke</c>, so we
	/// wrap it with a <see cref="TaskCompletionSource{T}"/> to get results back on the HTTP thread.
	/// </summary>
	sealed class DbgAccess {
		readonly DbgManager dbgManager;

		public DbgAccess(DbgManager dbgManager) => this.dbgManager = dbgManager;

		public DbgManager DbgManager => dbgManager;

		public T Invoke<T>(Func<T> func, int timeoutMs = 10000) {
			var dispatcher = dbgManager.Dispatcher;
			if (dispatcher.CheckAccess())
				return func();
			var tcs = new TaskCompletionSource<T>();
			dispatcher.BeginInvoke(() => {
				try {
					tcs.SetResult(func());
				}
				catch (Exception ex) {
					tcs.SetException(ex);
				}
			});
			bool completed;
			try {
				completed = tcs.Task.Wait(timeoutMs);
			}
			catch (AggregateException ae) when (ae.InnerException is not null) {
				// Task.Wait wraps a handler throw; surface the real message, not "One or more errors occurred".
				ExceptionDispatchInfo.Capture(ae.InnerException).Throw();
				throw; // unreachable
			}
			if (!completed) {
				// Observe any later fault so it doesn't become an unobserved-task exception.
				tcs.Task.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
				throw new TimeoutException($"Debugger thread did not respond within {timeoutMs} ms");
			}
			return tcs.Task.GetAwaiter().GetResult();
		}
	}
}
