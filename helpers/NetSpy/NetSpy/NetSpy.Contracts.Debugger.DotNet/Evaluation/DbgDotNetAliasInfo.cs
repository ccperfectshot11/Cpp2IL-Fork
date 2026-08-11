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
using NetSpy.Debugger.DotNet.Metadata;

namespace NetSpy.Contracts.Debugger.DotNet.Evaluation {
	/// <summary>
	/// Alias
	/// </summary>
	public readonly struct DbgDotNetAliasInfo {
		/// <summary>
		/// Alias kind
		/// </summary>
		public DbgDotNetAliasInfoKind Kind { get; }

		/// <summary>
		/// Alias type
		/// </summary>
		public DmdType Type { get; }

		/// <summary>
		/// Alias id
		/// </summary>
		public uint Id { get; }

		/// <summary>
		/// Custom type info understood by the EE or null
		/// </summary>
		public readonly DbgDotNetCustomTypeInfo? CustomTypeInfo;

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="kind">Alias kind</param>
		/// <param name="type">Alias type</param>
		/// <param name="id">Alias id</param>
		/// <param name="customTypeInfo">Custom type info understood by the EE or null</param>
		public DbgDotNetAliasInfo(DbgDotNetAliasInfoKind kind, DmdType type, uint id, DbgDotNetCustomTypeInfo? customTypeInfo) {
			Kind = kind;
			Type = type ?? throw new ArgumentNullException(nameof(type));
			Id = id;
			CustomTypeInfo = customTypeInfo;
		}
	}

	/// <summary>
	/// Alias kind
	/// </summary>
	public enum DbgDotNetAliasInfoKind {
		/// <summary>
		/// An exception, eg. "$exception"
		/// </summary>
		Exception,

		/// <summary>
		/// A stowed exception, eg. "$stowedexception"
		/// </summary>
		StowedException,

		/// <summary>
		/// A return value, eg. "$ReturnValue1"
		/// </summary>
		ReturnValue,
	}
}
