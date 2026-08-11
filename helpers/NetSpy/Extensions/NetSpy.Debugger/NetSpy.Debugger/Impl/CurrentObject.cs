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

namespace NetSpy.Debugger.Impl {
	readonly struct CurrentObject<T> : IEquatable<CurrentObject<T>> where T : DbgObject {
		public readonly T? Current;
		public readonly T? Break;
		public CurrentObject(T? current, T? @break) {
			Current = current;
			Break = @break;
		}
		public static bool operator ==(CurrentObject<T> left, CurrentObject<T> right) => left.Equals(right);
		public static bool operator !=(CurrentObject<T> left, CurrentObject<T> right) => !left.Equals(right);
		public bool Equals(CurrentObject<T> other) => Current == other.Current && Break == other.Break;
		public override bool Equals(object? obj) => obj is CurrentObject<T> other && Equals(other);
		public override int GetHashCode() => (Current?.GetHashCode() ?? 0) ^ (Break?.GetHashCode() ?? 0);
	}
}
