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
using NetSpy.Contracts.Debugger;
using NetSpy.Contracts.Images;
using NetSpy.Contracts.ToolBars;
using NetSpy.Contracts.Utilities;
using NetSpy.Debugger.Properties;

namespace NetSpy.Debugger.DbgUI {
	static class ToolbarButtonCommands {
		abstract class DebugToolBarButton : ToolBarButtonBase {
			// Prevents the debugger from being loaded since IsVisible will be called early
			[Export(typeof(IDbgManagerStartListener))]
			sealed class DbgManagerStartListener : IDbgManagerStartListener {
				public void OnStart(DbgManager dbgManager) => initd = true;
			}
			protected static bool initd;

			protected readonly Lazy<Debugger> debugger;

			protected DebugToolBarButton(Lazy<Debugger> debugger) => this.debugger = debugger;

			public override bool IsVisible(IToolBarItemContext context) => initd && debugger.Value.IsDebugging;
		}

		[ExportToolBarButton(Icon = DsImagesAttribute.Run, Header = "res:ToolBarStartDebuggingButton", Group = ToolBarConstants.GROUP_APP_TB_MAIN_DEBUG, Order = 0)]
		sealed class DebugAssemblyToolbarCommand : DebugToolBarButton {
			[ImportingConstructor]
			public DebugAssemblyToolbarCommand(Lazy<Debugger> debugger)
				: base(debugger) {
			}

			public override bool IsVisible(IToolBarItemContext context) => !initd || !debugger.Value.IsDebugging;
			public override void Execute(IToolBarItemContext context) => debugger.Value.DebugProgram(pauseAtEntryPoint: false);
			public override string? GetToolTip(IToolBarItemContext context) => ToolTipHelper.AddKeyboardShortcut(NetSpy_Debugger_Resources.ToolBarDebugAssemblyToolTip, NetSpy_Debugger_Resources.ShortCutKeyF5);
		}

		[ExportToolBarButton(Icon = DsImagesAttribute.Run, Header = "res:ToolBarContinueDebuggingButton", Group = ToolBarConstants.GROUP_APP_TB_MAIN_DEBUG_CONTINUE, Order = 0)]
		sealed class ContinueDebugToolBarButtonCommand : DebugToolBarButton {
			[ImportingConstructor]
			public ContinueDebugToolBarButtonCommand(Lazy<Debugger> debugger)
				: base(debugger) {
			}

			public override void Execute(IToolBarItemContext context) => debugger.Value.Continue();
			public override bool IsEnabled(IToolBarItemContext context) => debugger.Value.CanContinue;
			public override string? GetToolTip(IToolBarItemContext context) => ToolTipHelper.AddKeyboardShortcut(NetSpy_Debugger_Resources.ToolBarContinueDebuggingToolTip, NetSpy_Debugger_Resources.ShortCutKeyF5);
		}

		[ExportToolBarButton(Icon = DsImagesAttribute.Pause, Group = ToolBarConstants.GROUP_APP_TB_MAIN_DEBUG_CONTINUE, Order = 10)]
		sealed class BreakDebugToolBarButtonCommand : DebugToolBarButton {
			[ImportingConstructor]
			public BreakDebugToolBarButtonCommand(Lazy<Debugger> debugger)
				: base(debugger) {
			}

			public override void Execute(IToolBarItemContext context) => debugger.Value.BreakAll();
			public override bool IsEnabled(IToolBarItemContext context) => debugger.Value.CanBreakAll;
			public override string? GetToolTip(IToolBarItemContext context) => ToolTipHelper.AddKeyboardShortcut(NetSpy_Debugger_Resources.ToolBarBreakAllToolTip, NetSpy_Debugger_Resources.ShortCutKeyCtrlAltBreak);
		}

		[ExportToolBarButton(Icon = DsImagesAttribute.Stop, Group = ToolBarConstants.GROUP_APP_TB_MAIN_DEBUG_CONTINUE, Order = 20)]
		sealed class StopDebugToolBarButtonCommand : DebugToolBarButton {
			[ImportingConstructor]
			public StopDebugToolBarButtonCommand(Lazy<Debugger> debugger)
				: base(debugger) {
			}

			public override void Execute(IToolBarItemContext context) => debugger.Value.StopDebugging();
			public override bool IsEnabled(IToolBarItemContext context) => debugger.Value.CanStopDebugging;
			public override string? GetToolTip(IToolBarItemContext context) => ToolTipHelper.AddKeyboardShortcut(NetSpy_Debugger_Resources.ToolBarStopDebuggingToolTip, NetSpy_Debugger_Resources.ShortCutKeyShiftF5);
		}

		[ExportToolBarButton(Icon = DsImagesAttribute.Restart, Group = ToolBarConstants.GROUP_APP_TB_MAIN_DEBUG_CONTINUE, Order = 30)]
		sealed class RestartDebugToolBarButtonCommand : DebugToolBarButton {
			[ImportingConstructor]
			public RestartDebugToolBarButtonCommand(Lazy<Debugger> debugger)
				: base(debugger) {
			}

			public override void Execute(IToolBarItemContext context) => debugger.Value.Restart();
			public override bool IsEnabled(IToolBarItemContext context) => debugger.Value.CanRestart;
			public override string? GetToolTip(IToolBarItemContext context) => ToolTipHelper.AddKeyboardShortcut(NetSpy_Debugger_Resources.ToolBarRestartToolTip, NetSpy_Debugger_Resources.ShortCutKeyCtrlShiftF5);
		}

		[ExportToolBarButton(Icon = DsImagesAttribute.GoToNext, Group = ToolBarConstants.GROUP_APP_TB_MAIN_DEBUG_STEP, Order = 0)]
		sealed class ShowNextStatementDebugToolBarButtonCommand : DebugToolBarButton {
			[ImportingConstructor]
			public ShowNextStatementDebugToolBarButtonCommand(Lazy<Debugger> debugger)
				: base(debugger) {
			}

			public override void Execute(IToolBarItemContext context) => debugger.Value.ShowNextStatement();
			public override bool IsEnabled(IToolBarItemContext context) => debugger.Value.CanShowNextStatement;
			public override string? GetToolTip(IToolBarItemContext context) => ToolTipHelper.AddKeyboardShortcut(NetSpy_Debugger_Resources.ToolBarShowNextStatementToolTip, NetSpy_Debugger_Resources.ShortCutAltAsterisk);
		}

		[ExportToolBarButton(Icon = DsImagesAttribute.StepInto, Group = ToolBarConstants.GROUP_APP_TB_MAIN_DEBUG_STEP, Order = 10)]
		sealed class StepIntoDebugToolBarButtonCommand : DebugToolBarButton {
			[ImportingConstructor]
			public StepIntoDebugToolBarButtonCommand(Lazy<Debugger> debugger)
				: base(debugger) {
			}

			public override void Execute(IToolBarItemContext context) => debugger.Value.StepInto();
			public override bool IsEnabled(IToolBarItemContext context) => debugger.Value.CanStepInto;
			public override string? GetToolTip(IToolBarItemContext context) => ToolTipHelper.AddKeyboardShortcut(NetSpy_Debugger_Resources.ToolBarStepIntoToolTip, NetSpy_Debugger_Resources.ShortCutKeyF11);
		}

		[ExportToolBarButton(Icon = DsImagesAttribute.StepOver, Group = ToolBarConstants.GROUP_APP_TB_MAIN_DEBUG_STEP, Order = 20)]
		sealed class StepOverDebugToolBarButtonCommand : DebugToolBarButton {
			[ImportingConstructor]
			public StepOverDebugToolBarButtonCommand(Lazy<Debugger> debugger)
				: base(debugger) {
			}

			public override void Execute(IToolBarItemContext context) => debugger.Value.StepOver();
			public override bool IsEnabled(IToolBarItemContext context) => debugger.Value.CanStepOver;
			public override string? GetToolTip(IToolBarItemContext context) => ToolTipHelper.AddKeyboardShortcut(NetSpy_Debugger_Resources.ToolBarStepOverToolTip, NetSpy_Debugger_Resources.ShortCutKeyF10);
		}

		[ExportToolBarButton(Icon = DsImagesAttribute.StepOut, Group = ToolBarConstants.GROUP_APP_TB_MAIN_DEBUG_STEP, Order = 30)]
		sealed class StepOutDebugToolBarButtonCommand : DebugToolBarButton {
			[ImportingConstructor]
			public StepOutDebugToolBarButtonCommand(Lazy<Debugger> debugger)
				: base(debugger) {
			}

			public override void Execute(IToolBarItemContext context) => debugger.Value.StepOut();
			public override bool IsEnabled(IToolBarItemContext context) => debugger.Value.CanStepOut;
			public override string? GetToolTip(IToolBarItemContext context) => ToolTipHelper.AddKeyboardShortcut(NetSpy_Debugger_Resources.ToolBarStepOutToolTip, NetSpy_Debugger_Resources.ShortCutKeyShiftF11);
		}
	}
}
