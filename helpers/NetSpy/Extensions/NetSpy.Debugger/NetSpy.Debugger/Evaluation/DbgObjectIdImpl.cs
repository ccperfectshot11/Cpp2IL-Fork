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
using NetSpy.Contracts.Debugger.Engine.Evaluation;
using NetSpy.Contracts.Debugger.Evaluation;

namespace NetSpy.Debugger.Evaluation {
	sealed class DbgObjectIdImpl : DbgObjectId {
		public override DbgRuntime Runtime => owner.Runtime;
		public override uint Id => EngineObjectId.Id;
		public DbgEngineObjectId EngineObjectId { get; }

		readonly DbgRuntimeObjectIdServiceImpl owner;

		public DbgObjectIdImpl(DbgRuntimeObjectIdServiceImpl owner, DbgEngineObjectId engineObjectId) {
			this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
			EngineObjectId = engineObjectId ?? throw new ArgumentNullException(nameof(engineObjectId));
		}

		DbgValue CreateResult(DbgEngineValue engineValue) {
			var value = new DbgValueImpl(Runtime, engineValue);
			Runtime.CloseOnContinue(value);
			return value;
		}

		public override DbgValue GetValue(DbgEvaluationInfo evalInfo) {
			if (evalInfo is null)
				throw new ArgumentNullException(nameof(evalInfo));
			if (!(evalInfo.Context is DbgEvaluationContextImpl))
				throw new ArgumentException();
			if (evalInfo.Context.Runtime != Runtime)
				throw new ArgumentException();
			return CreateResult(EngineObjectId.GetValue(evalInfo));
		}

		public override void Remove() => owner.Remove(new[] { this });
		protected override void CloseCore(DbgDispatcher dispatcher) => EngineObjectId.Close(dispatcher);
	}
}
