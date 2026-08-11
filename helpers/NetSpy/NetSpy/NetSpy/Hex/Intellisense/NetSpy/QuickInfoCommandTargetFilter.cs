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
using System.ComponentModel.Composition;
using System.Diagnostics;
using NetSpy.Contracts.Command;
using NetSpy.Contracts.Hex.Editor;
using NetSpy.Contracts.Hex.Intellisense;

namespace NetSpy.Hex.Intellisense.NetSpy {
	[ExportCommandTargetFilterProvider(CommandTargetFilterOrder.HexDefaultIntellisenseQuickInfo)]
	sealed class QuickInfoCommandTargetFilterProvider : ICommandTargetFilterProvider {
		readonly HexQuickInfoBroker quickInfoBroker;

		[ImportingConstructor]
		QuickInfoCommandTargetFilterProvider(HexQuickInfoBroker quickInfoBroker) => this.quickInfoBroker = quickInfoBroker;

		public ICommandTargetFilter? Create(object target) {
			if (target is HexView hexView)
				return new QuickInfoCommandTargetFilter(quickInfoBroker, hexView);
			return null;
		}
	}

	sealed class QuickInfoCommandTargetFilter : ICommandTargetFilter {
		readonly HexQuickInfoBroker quickInfoBroker;
		readonly HexView hexView;
		HexQuickInfoSession? quickInfoSession;

		public QuickInfoCommandTargetFilter(HexQuickInfoBroker quickInfoBroker, HexView hexView) {
			this.quickInfoBroker = quickInfoBroker ?? throw new ArgumentNullException(nameof(quickInfoBroker));
			this.hexView = hexView ?? throw new ArgumentNullException(nameof(hexView));
		}

		public CommandTargetStatus CanExecute(Guid group, int cmdId) {
			if (group == CommandConstants.HexEditorGroup) {
				switch ((HexEditorIds)cmdId) {
				case HexEditorIds.QUICKINFO:
					return CommandTargetStatus.Handled;
				}
			}
			return CommandTargetStatus.NotHandled;
		}

		public CommandTargetStatus Execute(Guid group, int cmdId, object? args = null) {
			object? result = null;
			return Execute(group, cmdId, args, ref result);
		}

		public CommandTargetStatus Execute(Guid group, int cmdId, object? args, ref object? result) {
			if (group == CommandConstants.HexEditorGroup) {
				switch ((HexEditorIds)cmdId) {
				case HexEditorIds.QUICKINFO:
					TriggerQuickInfo();
					return CommandTargetStatus.Handled;
				}
			}
			return CommandTargetStatus.NotHandled;
		}

		void TriggerQuickInfo() {
			if (quickInfoSession is not null) {
				quickInfoSession.Dismissed -= QuickInfoSession_Dismissed;
				quickInfoSession.Dismiss();
				quickInfoSession = null;
			}
			quickInfoSession = quickInfoBroker.TriggerQuickInfo(hexView, hexView.Caret.Position.Position.ActivePosition, false);
			if (quickInfoSession?.IsDismissed == false)
				quickInfoSession.Dismissed += QuickInfoSession_Dismissed;
			else
				quickInfoSession = null;
		}

		void QuickInfoSession_Dismissed(object? sender, EventArgs e) {
			var qiSession = (HexQuickInfoSession)sender!;
			qiSession.Dismissed -= QuickInfoSession_Dismissed;
			Debug.Assert(qiSession == quickInfoSession);
			if (qiSession == quickInfoSession)
				quickInfoSession = null;
		}

		public void SetNextCommandTarget(ICommandTarget commandTarget) { }
		public void Dispose() { }
	}
}
