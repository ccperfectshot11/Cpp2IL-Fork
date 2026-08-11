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
using System.ComponentModel.Composition;
using NetSpy.Contracts.Debugger;
using NetSpy.Contracts.Debugger.DotNet.Evaluation;
using NetSpy.Contracts.Debugger.DotNet.Steppers.Engine;
using NetSpy.Contracts.Debugger.Engine.Steppers;
using NetSpy.Contracts.Debugger.Evaluation;
using NetSpy.Debugger.DotNet.Code;

namespace NetSpy.Debugger.DotNet.Steppers.Engine {
	[Export(typeof(DbgEngineStepperFactory))]
	sealed class DbgEngineStepperFactoryImpl : DbgEngineStepperFactory {
		readonly DbgLanguageService dbgLanguageService;
		readonly DbgDotNetDebugInfoService dbgDotNetDebugInfoService;
		readonly DebuggerSettings debuggerSettings;

		[ImportingConstructor]
		DbgEngineStepperFactoryImpl(DbgLanguageService dbgLanguageService, DbgDotNetDebugInfoService dbgDotNetDebugInfoService, DebuggerSettings debuggerSettings) {
			this.dbgLanguageService = dbgLanguageService;
			this.dbgDotNetDebugInfoService = dbgDotNetDebugInfoService;
			this.debuggerSettings = debuggerSettings;
		}

		public override DbgEngineStepper Create(IDbgDotNetRuntime runtime, DbgDotNetEngineStepper stepper, DbgThread thread) {
			if (runtime is null)
				throw new ArgumentNullException(nameof(runtime));
			if (stepper is null)
				throw new ArgumentNullException(nameof(stepper));
			if (thread is null)
				throw new ArgumentNullException(nameof(thread));
			return new DbgEngineStepperImpl(dbgLanguageService, dbgDotNetDebugInfoService, debuggerSettings, runtime, stepper, thread);
		}
	}
}
