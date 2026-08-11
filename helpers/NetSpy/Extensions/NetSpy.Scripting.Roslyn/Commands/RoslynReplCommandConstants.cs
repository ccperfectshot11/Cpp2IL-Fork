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
using NetSpy.Contracts.Command;

namespace NetSpy.Scripting.Roslyn.Commands {
	static class RoslynReplCommandConstants {
		/// <summary>
		/// Roslyn REPL command IDs (<see cref="RoslynReplIds"/>)
		/// </summary>
		public static readonly Guid RoslynReplGroup = new Guid("75758152-7214-4C5C-8F5C-180441233B46");

		/// <summary>
		/// Order of Roslyn REPL editor <see cref="ICommandInfoProvider"/>
		/// </summary>
		public const double CommandInfoProvider_RoslynREPL = CommandInfoProviderOrder.REPL - 100;

		/// <summary>
		/// Order of Roslyn REPL editor <see cref="ICommandTargetFilter"/>
		/// </summary>
		public const double CommandTargetFilter_RoslynREPL = CommandTargetFilterOrder.REPL - 100;
	}
}
