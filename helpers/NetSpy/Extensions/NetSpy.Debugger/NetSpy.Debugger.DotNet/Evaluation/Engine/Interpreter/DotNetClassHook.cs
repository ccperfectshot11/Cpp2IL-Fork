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

using NetSpy.Contracts.Debugger.DotNet.Evaluation;
using NetSpy.Debugger.DotNet.Interpreter;
using NetSpy.Debugger.DotNet.Metadata;

namespace NetSpy.Debugger.DotNet.Evaluation.Engine.Interpreter {
	abstract class DotNetClassHook {
		public virtual DbgDotNetValue? CreateInstance(DotNetClassHookCallOptions options, DmdConstructorInfo ctor, ILValue[] arguments) => null;
		public virtual DbgDotNetValue? Call(DotNetClassHookCallOptions options, DbgDotNetValue? objValue, DmdMethodBase method, ILValue[] arguments) => null;
	}

	enum DotNetClassHookCallOptions {
		None			= 0,
		IsCallvirt		= 0x00000001,
	}
}
