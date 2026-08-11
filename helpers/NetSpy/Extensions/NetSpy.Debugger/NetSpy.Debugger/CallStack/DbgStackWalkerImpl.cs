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
using System.Diagnostics;
using NetSpy.Contracts.Debugger;
using NetSpy.Contracts.Debugger.CallStack;
using NetSpy.Contracts.Debugger.Engine.CallStack;
using NetSpy.Debugger.Impl;

namespace NetSpy.Debugger.CallStack {
	sealed class DbgStackWalkerImpl : DbgStackWalker {
		public override DbgThread Thread => thread;
		DbgDispatcher Dispatcher => Thread.Process.DbgManager.Dispatcher;

		readonly DbgEngineStackWalker engineStackWalker;
		readonly DbgThreadImpl thread;

		public DbgStackWalkerImpl(DbgThreadImpl thread, DbgEngineStackWalker engineStackWalker) {
			this.thread = thread ?? throw new ArgumentNullException(nameof(thread));
			this.engineStackWalker = engineStackWalker ?? throw new ArgumentNullException(nameof(engineStackWalker));
			thread.AddAutoClose(this);
		}

		public override DbgStackFrame[] GetNextStackFrames(int maxFrames) {
			if (maxFrames < 0)
				throw new ArgumentOutOfRangeException(nameof(maxFrames));
			var engineFrames = engineStackWalker.GetNextStackFrames(maxFrames);
			Debug.Assert(engineFrames.Length <= maxFrames);
			var frames = new DbgStackFrame[engineFrames.Length];
			for (int i = 0; i < engineFrames.Length; i++)
				frames[i] = new DbgStackFrameImpl(thread, engineFrames[i]);
			Runtime.CloseOnContinue(frames);
			return frames;
		}

		protected override void CloseCore(DbgDispatcher dispatcher) {
			thread.RemoveAutoClose(this);
			engineStackWalker.Close(dispatcher);
		}
	}
}
