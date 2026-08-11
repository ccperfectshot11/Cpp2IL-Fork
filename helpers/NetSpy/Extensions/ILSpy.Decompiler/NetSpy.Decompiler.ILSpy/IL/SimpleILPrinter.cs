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

using System.ComponentModel.Composition;
using System.Text;
using dnlib.DotNet;
using NetSpy.Contracts.Decompiler;
using NetSpy.Contracts.Text;
using NetSpy.Decompiler.ILSpy.Core.IL;
using ICSharpCode.Decompiler.Disassembler;

namespace NetSpy.Decompiler.ILSpy.IL {
	[Export(typeof(ISimpleILPrinter))]
	sealed class SimpleILPrinter : ISimpleILPrinter {
		double ISimpleILPrinter.Order => -100;

		bool ISimpleILPrinter.Write(IDecompilerOutput output, IMemberRef? member) => ILDecompilerUtils.Write(output, member);
		void ISimpleILPrinter.Write(IDecompilerOutput output, MethodSig? sig) => output.Write(new StringBuilder(), sig);
		void ISimpleILPrinter.Write(IDecompilerOutput output, TypeSig? type) => type.WriteTo(output, new StringBuilder());
	}
}
