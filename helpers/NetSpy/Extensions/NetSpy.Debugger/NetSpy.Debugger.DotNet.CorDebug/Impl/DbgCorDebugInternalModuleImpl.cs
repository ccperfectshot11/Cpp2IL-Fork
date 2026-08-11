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
using NetSpy.Contracts.Debugger;
using NetSpy.Contracts.Debugger.DotNet;
using NetSpy.Debugger.DotNet.Metadata;

namespace NetSpy.Debugger.DotNet.CorDebug.Impl {
	sealed class DbgCorDebugInternalModuleImpl : DbgDotNetInternalModule {
		public override DmdModule? ReflectionModule { get; }
		public override DbgModule Module => module ?? throw new ArgumentNullException(nameof(module));
		DbgModule? module;
		readonly ClosedListenerCollection closedListenerCollection;
		public DbgCorDebugInternalModuleImpl(DmdModule reflectionModule, ClosedListenerCollection closedListenerCollection) {
			ReflectionModule = reflectionModule ?? throw new ArgumentNullException(nameof(reflectionModule));
			this.closedListenerCollection = closedListenerCollection ?? throw new ArgumentNullException(nameof(closedListenerCollection));
		}
		internal void SetModule(DbgModule module) {
			this.module = module ?? throw new ArgumentNullException(nameof(module));
			ReflectionModule!.GetOrCreateData(() => module);
		}
		internal void Remove() {
			var asm = ReflectionModule!.Assembly;
			asm.Remove(ReflectionModule);
			if (asm.GetModules().Length == 0)
				asm.AppDomain.Remove(asm);
		}
		protected override void CloseCore(DbgDispatcher dispatcher) => closedListenerCollection.RaiseClosed();
	}

	sealed class ClosedListenerCollection {
		public event EventHandler? Closed;
		public void RaiseClosed() => Closed?.Invoke(this, EventArgs.Empty);
	}
}
