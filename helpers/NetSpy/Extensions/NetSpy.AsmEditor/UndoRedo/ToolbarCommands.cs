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

using NetSpy.AsmEditor.Properties;
using NetSpy.Contracts.Images;
using NetSpy.Contracts.ToolBars;
using NetSpy.Contracts.Utilities;

namespace NetSpy.AsmEditor.UndoRedo {
	[ExportToolBarButton(OwnerGuid = ToolBarConstants.APP_TB_GUID, Icon = DsImagesAttribute.Undo, Group = ToolBarConstants.GROUP_APP_TB_MAIN_ASMED_UNDO, Order = 0)]
	sealed class UndoAsmEdCommand : ToolBarButtonCommand {
		public UndoAsmEdCommand()
			: base(UndoRoutedCommands.Undo) {
		}
		public override string? GetToolTip(IToolBarItemContext context) => ToolTipHelper.AddKeyboardShortcut(NetSpy_AsmEditor_Resources.UndoToolBarToolTip, NetSpy_AsmEditor_Resources.ShortCutKeyCtrlZ);
	}

	[ExportToolBarButton(OwnerGuid = ToolBarConstants.APP_TB_GUID, Icon = DsImagesAttribute.Redo, Group = ToolBarConstants.GROUP_APP_TB_MAIN_ASMED_UNDO, Order = 10)]
	sealed class RedoAsmEdCommand : ToolBarButtonCommand {
		public RedoAsmEdCommand()
			: base(UndoRoutedCommands.Redo) {
		}
		public override string? GetToolTip(IToolBarItemContext context) => ToolTipHelper.AddKeyboardShortcut(NetSpy_AsmEditor_Resources.RedoToolBarToolTip, NetSpy_AsmEditor_Resources.ShortCutKeyCtrlY);
	}
}
