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
using Mono.Debugger.Soft;

namespace NetSpy.Debugger.DotNet.Mono.Impl {
	sealed class DbgMonoDebugInternalAppDomainImpl : DbgDotNetInternalAppDomain {
		public override DmdAppDomain ReflectionAppDomain { get; }
		public override DbgAppDomain AppDomain => appDomain ?? throw new InvalidOperationException();
		DbgAppDomain? appDomain;
		public DbgMonoDebugInternalAppDomainImpl(DmdAppDomain reflectionAppDomain, AppDomainMirror monoAppDomain) {
			ReflectionAppDomain = reflectionAppDomain ?? throw new ArgumentNullException(nameof(reflectionAppDomain));
			reflectionAppDomain.GetOrCreateData(() => monoAppDomain);
		}
		internal static AppDomainMirror GetAppDomainMirror(DmdAppDomain reflectionAppDomain) => reflectionAppDomain.GetData<AppDomainMirror>();
		internal void SetAppDomain(DbgAppDomain appDomain) {
			this.appDomain = appDomain ?? throw new ArgumentNullException(nameof(appDomain));
			ReflectionAppDomain.GetOrCreateData(() => appDomain);
		}
		protected override void CloseCore(DbgDispatcher dispatcher) { }
	}
}
