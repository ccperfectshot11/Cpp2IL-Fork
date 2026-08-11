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
using NetSpy.Contracts.Debugger.DotNet.Evaluation;
using NetSpy.Contracts.Debugger.Engine.Evaluation;
using NetSpy.Contracts.Debugger.Evaluation;

namespace NetSpy.Debugger.DotNet.Evaluation.Engine {
	sealed class DbgEngineValueImpl : DbgEngineValue {
		public override object InternalValue => value;
		public override DbgSimpleValueType ValueType => value.GetRawValue().ValueType;
		public override bool HasRawValue => value.GetRawValue().HasRawValue;
		public override object? RawValue => value.GetRawValue().RawValue;
		internal DbgDotNetValue DotNetValue => value;

		readonly DbgDotNetValue value;

		public DbgEngineValueImpl(DbgDotNetValue value) => this.value = value ?? throw new ArgumentNullException(nameof(value));

		public override DbgRawAddressValue? GetRawAddressValue(bool onlyDataAddress) => value.GetRawAddressValue(onlyDataAddress);
		protected override void CloseCore(DbgDispatcher dispatcher) => value.Dispose();
	}
}
