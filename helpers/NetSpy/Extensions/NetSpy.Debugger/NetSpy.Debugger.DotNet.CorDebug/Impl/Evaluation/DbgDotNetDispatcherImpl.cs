/*
    Copyright (C) 2014-2019 de4dot@gmail.com

    This file is part of NetSpy

    NetSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    NetSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with NetSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using NetSpy.Contracts.Debugger.DotNet.Evaluation;

namespace NetSpy.Debugger.DotNet.CorDebug.Impl.Evaluation {
	sealed class DbgDotNetDispatcherImpl : DbgDotNetDispatcher {
		readonly DbgEngineImpl engine;

		public DbgDotNetDispatcherImpl(DbgEngineImpl engine) =>
			this.engine = engine ?? throw new ArgumentNullException(nameof(engine));

		public override void BeginInvoke(Action callback) => engine.CorDebugThread(callback);
		public override bool CheckAccess() => engine.CheckCorDebugThread();

		public override T Invoke<T>(Func<T> callback) {
			System.Diagnostics.Debugger.NotifyOfCrossThreadDependency();
			return engine.InvokeCorDebugThread(callback);
		}
	}
}
