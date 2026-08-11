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

using NetSpy.Contracts.Documents.Tabs.DocViewer.ToolTips;
using NetSpy.Contracts.Text;
using NetSpy.Properties;
using Iced.Intel;

namespace NetSpy.Disassembly.Viewer.X86 {
	[ExportDocumentViewerToolTipProvider]
	sealed class DocumentViewerToolTipProvider : IDocumentViewerToolTipProvider {
		public object? Create(IDocumentViewerToolTipProviderContext context, object? @ref) {
			switch (@ref) {
			case MnemonicReference mnemonicRef:
				return Create(context, mnemonicRef);

			default:
				return null;
			}
		}

		static object? Create(IDocumentViewerToolTipProviderContext context, MnemonicReference mnemonicRef) {
			var provider = context.Create();

			var opCode = mnemonicRef.Code.ToOpCode();

			provider.Output.Write(BoxedTextColor.Text, $"{NetSpy_Resources.ToolTip_OpCode}: {opCode.ToOpCodeString()}");
			provider.Output.WriteLine();
			provider.Output.Write(BoxedTextColor.Text, $"{NetSpy_Resources.ToolTip_Instruction}: {opCode.ToInstructionString()}");
			provider.Output.WriteLine();
			provider.Output.Write(BoxedTextColor.Text, $"CPUID: {string.Join(", ", mnemonicRef.CpuidFeatures)}");

			return provider.Create();
		}
	}
}
