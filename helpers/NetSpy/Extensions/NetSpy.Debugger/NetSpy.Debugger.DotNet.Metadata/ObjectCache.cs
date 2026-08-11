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

using System.Text;
using System.Threading;

namespace NetSpy.Debugger.DotNet.Metadata {
	static class ObjectCache {
		const int MAX_STRINGBUILDER_CAPACITY = 1024;
		static volatile StringBuilder? stringBuilder;
		public static StringBuilder AllocStringBuilder() => Interlocked.Exchange(ref stringBuilder, null) ?? new StringBuilder();
		public static void Free(ref StringBuilder? sb) {
			if (sb!.Capacity <= MAX_STRINGBUILDER_CAPACITY) {
				sb.Clear();
				stringBuilder = sb;
			}
			sb = null;
		}
		public static string FreeAndToString(ref StringBuilder? sb) {
			var res = sb!.ToString();
			Free(ref sb);
			return res;
		}
	}
}
