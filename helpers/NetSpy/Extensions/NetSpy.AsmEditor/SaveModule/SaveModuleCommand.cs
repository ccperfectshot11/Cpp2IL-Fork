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
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Windows.Input;
using NetSpy.AsmEditor.Commands;
using NetSpy.AsmEditor.Hex;
using NetSpy.AsmEditor.Properties;
using NetSpy.AsmEditor.UndoRedo;
using NetSpy.Contracts.Controls;
using NetSpy.Contracts.Documents.Tabs;
using NetSpy.Contracts.Documents.TreeView;
using NetSpy.Contracts.Extension;
using NetSpy.Contracts.Images;
using NetSpy.Contracts.Menus;
using NetSpy.Contracts.ToolBars;
using NetSpy.Contracts.Utilities;

namespace NetSpy.AsmEditor.SaveModule {
	[ExportAutoLoaded]
	sealed class SaveModuleCommandLoader : IAutoLoaded {
		public static readonly RoutedCommand SaveAllCommand = new RoutedCommand("SaveAll", typeof(SaveModuleCommandLoader));
		readonly Lazy<IUndoCommandService> undoCommandService;
		readonly Lazy<IDocumentSaver> documentSaver;

		[ImportingConstructor]
		SaveModuleCommandLoader(IWpfCommandService wpfCommandService, Lazy<IUndoCommandService> undoCommandService, Lazy<IDocumentSaver> documentSaver) {
			this.undoCommandService = undoCommandService;
			this.documentSaver = documentSaver;

			var cmds = wpfCommandService.GetCommands(ControlConstants.GUID_MAINWINDOW);
			cmds.Add(SaveAllCommand, (s, e) => SaveAll_Execute(), (s, e) => e.CanExecute = SaveAll_CanExecute, ModifierKeys.Control | ModifierKeys.Shift, Key.S);
		}

		object[] GetDirtyDocs() => undoCommandService.Value.GetModifiedDocuments().ToArray();
		bool SaveAll_CanExecute => undoCommandService.Value.CachedHasModifiedDocuments;
		void SaveAll_Execute() => documentSaver.Value.Save(GetDirtyDocs());
	}

	[ExportToolBarButton(Icon = DsImagesAttribute.SaveAll, Group = ToolBarConstants.GROUP_APP_TB_MAIN_OPEN, Order = 10)]
	sealed class SaveAllToolbarCommand : ToolBarButtonCommand {
		SaveAllToolbarCommand()
			: base(SaveModuleCommandLoader.SaveAllCommand) {
		}
		public override string? GetToolTip(IToolBarItemContext context) => ToolTipHelper.AddKeyboardShortcut(NetSpy_AsmEditor_Resources.SaveAllToolBarToolTip, NetSpy_AsmEditor_Resources.ShortCutKeyCtrlShiftS);
	}

	[ExportMenuItem(OwnerGuid = MenuConstants.APP_MENU_FILE_GUID, Header = "res:SaveAllCommand", Icon = DsImagesAttribute.SaveAll, InputGestureText = "res:ShortCutKeyCtrlShiftS", Group = MenuConstants.GROUP_APP_MENU_FILE_SAVE, Order = 30)]
	sealed class SaveAllCommand : MenuItemCommand {
		SaveAllCommand()
			: base(SaveModuleCommandLoader.SaveAllCommand) {
		}
	}

	[ExportMenuItem(OwnerGuid = MenuConstants.APP_MENU_FILE_GUID, Group = MenuConstants.GROUP_APP_MENU_FILE_SAVE, Order = 20)]
	sealed class SaveModuleCommand : FileMenuHandler {
		readonly IDocumentTabService documentTabService;
		readonly Lazy<IUndoCommandService> undoCommandService;
		readonly Lazy<IHexBufferService> hexBufferService;
		readonly Lazy<IDocumentSaver> documentSaver;

		[ImportingConstructor]
		SaveModuleCommand(IDocumentTabService documentTabService, Lazy<IUndoCommandService> undoCommandService, Lazy<IHexBufferService> hexBufferService, Lazy<IDocumentSaver> documentSaver)
			: base(documentTabService.DocumentTreeView) {
			this.documentTabService = documentTabService;
			this.undoCommandService = undoCommandService;
			this.hexBufferService = hexBufferService;
			this.documentSaver = documentSaver;
		}

		HashSet<object> GetDocuments(DocumentTreeNodeData[] nodes) {
			var hash = new HashSet<object>();

			foreach (var node in nodes) {
				var fileNode = node.GetDocumentNode();
				if (fileNode is null)
					continue;

				// Removed nodes could still be used, don't use them.
				var topNode = fileNode.GetTopNode();
				if (topNode is null || topNode.TreeNode.Parent is null)
					continue;

				bool added = false;

				if (fileNode.Document.ModuleDef is not null) {
					var file = fileNode.Document;
					var uo = undoCommandService.Value.GetUndoObject(file)!;
					if (undoCommandService.Value.IsModified(uo)) {
						hash.Add(file);
						added = true;
					}
				}

				var doc = hexBufferService.Value.TryGet(fileNode.Document.Filename);
				if (doc is not null) {
					var uo = undoCommandService.Value.GetUndoObject(doc)!;
					if (undoCommandService.Value.IsModified(uo)) {
						hash.Add(doc);
						added = true;
					}
				}

				// If nothing was modified, just include the selected module
				if (!added && fileNode.Document.ModuleDef is not null)
					hash.Add(fileNode.Document);
			}
			return new HashSet<object>(undoCommandService.Value.GetUniqueDocuments(hash));
		}

		public override bool IsVisible(AsmEditorContext context) => true;
		public override bool IsEnabled(AsmEditorContext context) => GetDocuments(context.Nodes).Count > 0;

		public override void Execute(AsmEditorContext context) {
			var asmNodes = GetDocuments(context.Nodes);
			documentSaver.Value.Save(asmNodes);
		}

		public override string? GetHeader(AsmEditorContext context) => GetDocuments(context.Nodes).Count <= 1 ? NetSpy_AsmEditor_Resources.SaveModuleCommand : NetSpy_AsmEditor_Resources.SaveModulesCommand;
	}
}
