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

using System.Collections.Generic;
using System.Collections.ObjectModel;
using NetSpy.Contracts.Debugger.DotNet.Evaluation.ValueNodes;
using NetSpy.Contracts.Debugger.DotNet.Text;
using NetSpy.Contracts.Debugger.Evaluation;
using NetSpy.Debugger.DotNet.Metadata;

namespace NetSpy.Roslyn.Debugger.ValueNodes {
	abstract class DbgDotNetValueNodeProvider {
		public abstract DbgDotNetText Name { get; }
		public abstract string Expression { get; }
		public abstract string ImageName { get; }
		public virtual DbgDotNetText ValueText => default;

		public abstract bool? HasChildren { get; }
		public abstract ulong GetChildCount(DbgEvaluationInfo evalInfo);
		public abstract DbgDotNetValueNode[] GetChildren(LanguageValueNodeFactory valueNodeFactory, DbgEvaluationInfo evalInfo, ulong index, int count, DbgValueNodeEvaluationOptions options, ReadOnlyCollection<string>? formatSpecifiers);

		public abstract void Dispose();

		public static DbgDotNetValueNodeProvider? Create(List<DbgDotNetValueNodeProvider> providers) {
			if (providers.Count == 0)
				return null;
			if (providers.Count == 1)
				return providers[0];
			return new AggregateValueNodeProvider(providers.ToArray());
		}

		protected static bool NeedCast(DmdType slotType, DmdType memberDeclaringType) {
			if (slotType.IsInterface)
				return true;
			else
				return !slotType.CanCastTo(memberDeclaringType);
		}
	}
}
