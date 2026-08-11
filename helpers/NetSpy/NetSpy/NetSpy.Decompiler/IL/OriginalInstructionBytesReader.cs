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

using dnlib.DotNet;
using dnlib.IO;
using NetSpy.Contracts.Decompiler;

namespace NetSpy.Decompiler.IL {
	sealed class OriginalInstructionBytesReader : IInstructionBytesReader {
		readonly bool hasReader;
		DataReader reader;

		public bool IsOriginalBytes => true;

		public OriginalInstructionBytesReader(MethodDef method) {
			//TODO: This fails and returns null if it's a CorMethodDef!
			//TODO: Support CorModuleDef
			if (method.Module is ModuleDefMD m) {
				reader = m.Metadata.PEImage.CreateReader(method.RVA + method.Body.HeaderSize);
				hasReader = true;
			}
		}

		public int ReadByte() {
			if (hasReader)
				return reader.ReadByte();
			return -1;
		}

		public void SetInstruction(int index, uint offset) {
			if (hasReader)
				reader.Position = offset;
		}
	}
}
