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

namespace NetSpy.Roslyn.Debugger.ExpressionCompiler.CSharp {
	static class MonoGeneratedNamesHelpers {
		public static bool TryGetHoistedLocalSlotIndex(string name, out int slotIndex) {
			int index = name.LastIndexOf('_');
			if (index >= 2 && name[index - 1] == '_' && name[0] == '<' && name[index - 2] == '>') {
				if (int.TryParse(name.Substring(index + 1), out slotIndex))
					return true;
			}
			slotIndex = -1;
			return false;
		}
	}
}
