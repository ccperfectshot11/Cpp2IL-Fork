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
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NetSpy.Contracts.App;
using NetSpy.Contracts.Controls;
using NetSpy.Contracts.MVVM;
using NetSpy.Contracts.Utilities;
using NetSpy.Debugger.Properties;

namespace NetSpy.Debugger.ToolWindows.Processes {
	interface IProcessesContent : IUIObjectProvider {
		void OnShow();
		void OnClose();
		void OnVisible();
		void OnHidden();
		void Focus();
		void FocusSearchTextBox();
		ListView ListView { get; }
		ProcessesOperations Operations { get; }
	}

	[Export(typeof(IProcessesContent))]
	sealed class ProcessesContent : IProcessesContent {
		public object? UIObject => processesControl;
		public IInputElement? FocusedElement => processesControl.ListView;
		public FrameworkElement? ZoomElement => processesControl;
		public ListView ListView => processesControl.ListView;
		public ProcessesOperations Operations { get; }

		readonly ProcessesControl processesControl;
		readonly IProcessesVM processesVM;

		sealed class ControlVM : ViewModelBase {
			public IProcessesVM VM { get; }
			ProcessesOperations Operations { get; }

			public string ContinueProcessToolTip => NetSpy_Debugger_Resources.Processes_ContinueProcessToolTip;
			public string BreakProcessToolTip => NetSpy_Debugger_Resources.Processes_BreakProcessToolTip;
			public string StepIntoProcessToolTip => $"{NetSpy_Debugger_Resources.Processes_StepIntoProcessToolTip} ({NetSpy_Debugger_Resources.StepsOnlyTheCurrentProcess})";
			public string StepOverProcessToolTip => $"{NetSpy_Debugger_Resources.Processes_StepOverProcessToolTip} ({NetSpy_Debugger_Resources.StepsOnlyTheCurrentProcess})";
			public string StepOutProcessToolTip => $"{NetSpy_Debugger_Resources.Processes_StepOutProcessToolTip} ({NetSpy_Debugger_Resources.StepsOnlyTheCurrentProcess})";
			public string DetachToolTip => ToolTipHelper.AddKeyboardShortcut(NetSpy_Debugger_Resources.Processes_DetachToolTip, null);
			public string TerminateToolTip => ToolTipHelper.AddKeyboardShortcut(NetSpy_Debugger_Resources.Processes_TerminateToolTip, null);
			public string AttachToProcessToolTip => ToolTipHelper.AddKeyboardShortcut(NetSpy_Debugger_Resources.Processes_AttachToProcessToolTip, NetSpy_Debugger_Resources.ShortCutKeyCtrlAltP);
			public string SearchToolTip => ToolTipHelper.AddKeyboardShortcut(NetSpy_Debugger_Resources.Processes_Search_ToolTip, NetSpy_Debugger_Resources.ShortCutKeyCtrlF);
			public string ResetSearchSettingsToolTip => ToolTipHelper.AddKeyboardShortcut(NetSpy_Debugger_Resources.Processes_ResetSearchSettings_ToolTip, null);
			public string SearchHelpToolTip => ToolTipHelper.AddKeyboardShortcut(NetSpy_Debugger_Resources.SearchHelp_ToolTip, null);

			public ICommand ContinueProcessCommand => new RelayCommand(a => Operations.ContinueProcess(), a => Operations.CanContinueProcess);
			public ICommand BreakProcessCommand => new RelayCommand(a => Operations.BreakProcess(), a => Operations.CanBreakProcess);
			public ICommand StepIntoProcessCommand => new RelayCommand(a => Operations.StepIntoProcess(), a => Operations.CanStepIntoProcess);
			public ICommand StepOverProcessCommand => new RelayCommand(a => Operations.StepOverProcess(), a => Operations.CanStepOverProcess);
			public ICommand StepOutProcessCommand => new RelayCommand(a => Operations.StepOutProcess(), a => Operations.CanStepOutProcess);
			public ICommand DetachCommand => new RelayCommand(a => Operations.DetachProcess(), a => Operations.CanDetachProcess);
			public ICommand TerminateCommand => new RelayCommand(a => Operations.TerminateProcess(), a => Operations.CanTerminateProcess);
			public ICommand AttachToProcessCommand => new RelayCommand(a => Operations.AttachToProcess(), a => Operations.CanAttachToProcess);
			public ICommand ResetSearchSettingsCommand => new RelayCommand(a => Operations.ResetSearchSettings(), a => Operations.CanResetSearchSettings);
			public ICommand SearchHelpCommand => new RelayCommand(a => SearchHelp());

			readonly IMessageBoxService messageBoxService;
			readonly DependencyObject control;

			public ControlVM(IProcessesVM vm, ProcessesOperations operations, IMessageBoxService messageBoxService, DependencyObject control) {
				VM = vm;
				Operations = operations;
				this.messageBoxService = messageBoxService;
				this.control = control;
			}

			void SearchHelp() => messageBoxService.Show(VM.GetSearchHelpText(), ownerWindow: Window.GetWindow(control));
		}

		[ImportingConstructor]
		ProcessesContent(IWpfCommandService wpfCommandService, IProcessesVM processesVM, ProcessesOperations processesOperations, IMessageBoxService messageBoxService) {
			Operations = processesOperations;
			processesControl = new ProcessesControl();
			this.processesVM = processesVM;
			processesControl.DataContext = new ControlVM(processesVM, processesOperations, messageBoxService, processesControl);
			processesControl.ProcessesListViewDoubleClick += ProcessesControl_ProcessesListViewDoubleClick;

			wpfCommandService.Add(ControlConstants.GUID_DEBUGGER_PROCESSES_CONTROL, processesControl);
			wpfCommandService.Add(ControlConstants.GUID_DEBUGGER_PROCESSES_LISTVIEW, processesControl.ListView);
		}

		void ProcessesControl_ProcessesListViewDoubleClick(object? sender, EventArgs e) {
			bool newTab = Keyboard.Modifiers == ModifierKeys.Shift || Keyboard.Modifiers == ModifierKeys.Control;
			if (Operations.CanSetCurrentProcess)
				Operations.SetCurrentProcess(newTab);
		}

		public void FocusSearchTextBox() => processesControl.FocusSearchTextBox();
		public void Focus() => UIUtilities.FocusSelector(processesControl.ListView);
		public void OnClose() => processesVM.IsOpen = false;
		public void OnShow() => processesVM.IsOpen = true;
		public void OnHidden() => processesVM.IsVisible = false;
		public void OnVisible() => processesVM.IsVisible = true;
	}
}
