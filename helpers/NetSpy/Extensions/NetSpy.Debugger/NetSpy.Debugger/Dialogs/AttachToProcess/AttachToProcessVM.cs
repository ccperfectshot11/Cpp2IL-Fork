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
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using NetSpy.Contracts.Debugger;
using NetSpy.Contracts.Debugger.Attach.Dialogs;
using NetSpy.Contracts.Debugger.Text;
using NetSpy.Contracts.MVVM;
using NetSpy.Contracts.Settings.AppearanceCategory;
using NetSpy.Contracts.Text.Classification;
using NetSpy.Contracts.ToolWindows.Search;
using NetSpy.Contracts.Utilities;
using NetSpy.Debugger.Properties;
using NetSpy.Debugger.UI;
using NetSpy.Debugger.Utilities;
using Microsoft.VisualStudio.Text.Classification;

namespace NetSpy.Debugger.Dialogs.AttachToProcess {
	sealed class AttachToProcessVM : ViewModelBase, IGridViewColumnDescsProvider, IComparer<ProgramVM> {
		readonly ObservableCollection<ProgramVM> realAllItems;
		public BulkObservableCollection<ProgramVM> AllItems { get; }
		public ObservableCollection<ProgramVM> SelectedItems { get; }
		public GridViewColumnDescs Descs { get; }

		public string SearchToolTip => ToolTipHelper.AddKeyboardShortcut(NetSpy_Debugger_Resources.AttachToProcess_Search_ToolTip, NetSpy_Debugger_Resources.ShortCutKeyCtrlF);
		public ICommand SearchHelpCommand => new RelayCommand(a => searchHelp());

		public ICommand RefreshCommand => new RelayCommand(a => Refresh(), a => CanRefresh);
		public string SearchHelpToolTip => ToolTipHelper.AddKeyboardShortcut(NetSpy_Debugger_Resources.SearchHelp_ToolTip, null);

		public ICommand InfoLinkCommand => new RelayCommand(a => ShowInfoLinkPage());
		public bool HasInfoLink => InfoLinkToolTip is not null && infoLink is not null;
		public string? InfoLinkToolTip { get; }
		readonly string? infoLink;

		public string Title { get; }

		public override bool HasError => hasError;
		bool hasError;

		void UpdateHasError() {
			var value = SelectedItems.Count == 0;
			if (hasError != value) {
				hasError = value;
				HasErrorUpdated();
			}
		}

		public bool HasMessageText => !string.IsNullOrEmpty(MessageText);
		public string? MessageText { get; }

		public string FilterText {
			get => filterText;
			set {
				if (filterText == value)
					return;
				filterText = value;
				OnPropertyChanged(nameof(FilterText));
				FilterList(filterText);
			}
		}
		string filterText = string.Empty;

		readonly UIDispatcher uiDispatcher;
		readonly DbgManager dbgManager;
		readonly AttachProgramOptionsAggregatorFactory attachProgramOptionsAggregatorFactory;
		readonly AttachToProcessContext attachToProcessContext;
		readonly Action searchHelp;
		readonly string[]? providerNames;
		AttachProgramOptionsAggregator? attachProgramOptionsAggregator;
		ProcessProvider? processProvider;

		public AttachToProcessVM(ShowAttachToProcessDialogOptions? options, UIDispatcher uiDispatcher, DbgManager dbgManager, DebuggerSettings debuggerSettings, ProgramFormatterProvider programFormatterProvider, IClassificationFormatMapService classificationFormatMapService, ITextElementProvider textElementProvider, AttachProgramOptionsAggregatorFactory attachProgramOptionsAggregatorFactory, Action searchHelp) {
			if (options is null) {
				options = new ShowAttachToProcessDialogOptions();
				options.InfoLink = new AttachToProcessLinkInfo {
					ToolTipMessage = NetSpy_Debugger_Resources.AttachToProcess_MakingAnImageEasierToDebug,
					Url = "https://github.com/NetSpyEx/NetSpy/wiki/Making-an-Image-Easier-to-Debug",
				};
			}
			Title = GetTitle(options);
			MessageText = GetMessage(options);
			if (options.InfoLink is not null) {
				var l = options.InfoLink.Value;
				if (!string.IsNullOrEmpty(l.Url)) {
					InfoLinkToolTip = l.ToolTipMessage;
					infoLink = l.Url;
				}
			}

			providerNames = options.ProviderNames;
			realAllItems = new ObservableCollection<ProgramVM>();
			AllItems = new BulkObservableCollection<ProgramVM>();
			SelectedItems = new ObservableCollection<ProgramVM>();
			SelectedItems.CollectionChanged += (_, e) => { UpdateHasError(); };
			this.uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
			uiDispatcher.VerifyAccess();
			this.dbgManager = dbgManager ?? throw new ArgumentNullException(nameof(dbgManager));
			this.attachProgramOptionsAggregatorFactory = attachProgramOptionsAggregatorFactory ?? throw new ArgumentNullException(nameof(attachProgramOptionsAggregatorFactory));
			var classificationFormatMap = classificationFormatMapService.GetClassificationFormatMap(AppearanceCategoryConstants.UIMisc);
			attachToProcessContext = new AttachToProcessContext(classificationFormatMap, textElementProvider, new SearchMatcher(searchColumnDefinitions), programFormatterProvider.Create());
			this.searchHelp = searchHelp ?? throw new ArgumentNullException(nameof(searchHelp));

			attachToProcessContext.SyntaxHighlight = debuggerSettings.SyntaxHighlight;

			Descs = new GridViewColumnDescs {
				Columns = new GridViewColumnDesc[] {
					new GridViewColumnDesc(AttachToProcessWindowColumnIds.Process, NetSpy_Debugger_Resources.Column_Process),
					new GridViewColumnDesc(AttachToProcessWindowColumnIds.ProcessID, NetSpy_Debugger_Resources.Column_ProcessID),
					new GridViewColumnDesc(AttachToProcessWindowColumnIds.ProcessTitle, NetSpy_Debugger_Resources.Column_ProcessTitle),
					new GridViewColumnDesc(AttachToProcessWindowColumnIds.ProcessType, NetSpy_Debugger_Resources.Column_ProcessType),
					new GridViewColumnDesc(AttachToProcessWindowColumnIds.ProcessArchitecture, NetSpy_Debugger_Resources.Column_ProcessArchitecture),
					new GridViewColumnDesc(AttachToProcessWindowColumnIds.ProcessFilename, NetSpy_Debugger_Resources.Column_ProcessFilename),
					new GridViewColumnDesc(AttachToProcessWindowColumnIds.ProcessCommandLine, NetSpy_Debugger_Resources.Column_ProcessCommandLine),
				},
			};
			Descs.SortedColumnChanged += (a, b) => SortList();

			UpdateHasError();
			RefreshCore();
		}

		// Don't change the order of these instances without also updating input passed to SearchMatcher.IsMatchAll()
		static readonly SearchColumnDefinition[] searchColumnDefinitions = new SearchColumnDefinition[] {
			new SearchColumnDefinition(PredefinedTextClassifierTags.AttachToProcessWindowProcess, "p", NetSpy_Debugger_Resources.Column_Process),
			new SearchColumnDefinition(PredefinedTextClassifierTags.AttachToProcessWindowPid, "i", NetSpy_Debugger_Resources.Column_ProcessID),
			new SearchColumnDefinition(PredefinedTextClassifierTags.AttachToProcessWindowTitle, "t", NetSpy_Debugger_Resources.Column_ProcessTitle),
			new SearchColumnDefinition(PredefinedTextClassifierTags.AttachToProcessWindowType, "T", NetSpy_Debugger_Resources.Column_ProcessType),
			new SearchColumnDefinition(PredefinedTextClassifierTags.AttachToProcessWindowMachine, "a", NetSpy_Debugger_Resources.Column_ProcessArchitecture),
			new SearchColumnDefinition(PredefinedTextClassifierTags.AttachToProcessWindowFullPath, "f", NetSpy_Debugger_Resources.Column_ProcessFilename),
			new SearchColumnDefinition(PredefinedTextClassifierTags.AttachToProcessWindowCommandLine, "c", NetSpy_Debugger_Resources.Column_ProcessCommandLine),
		};

		static string GetTitle(ShowAttachToProcessDialogOptions options) {
			var s = options.Title ?? NetSpy_Debugger_Resources.Attach_AttachToProcess;
			if (!string.IsNullOrEmpty(options.ProcessType))
				return s + " (" + options.ProcessType + ")";
			return s;
		}

		static string? GetMessage(ShowAttachToProcessDialogOptions options) {
			if (options.Message is not null)
				return options.Message;
			if (!Environment.Is64BitOperatingSystem)
				return null;
			return IntPtr.Size == 4 ? NetSpy_Debugger_Resources.Attach_UseNetSpy32 : NetSpy_Debugger_Resources.Attach_UseNetSpy64;
		}

		public string GetSearchHelpText() => attachToProcessContext.SearchMatcher.GetHelpText();

		public bool IsRefreshing => !CanRefresh;
		bool CanRefresh => attachProgramOptionsAggregator is null;
		void Refresh() {
			uiDispatcher.VerifyAccess();
			if (!CanRefresh)
				return;
			RefreshCore();
		}

		void RefreshCore() {
			uiDispatcher.VerifyAccess();
			RemoveAggregator();
			ClearAllItems();
			processProvider = new ProcessProvider();
			attachProgramOptionsAggregator = attachProgramOptionsAggregatorFactory.Create(providerNames);
			attachProgramOptionsAggregator.AttachProgramOptionsAdded += AttachProgramOptionsAggregator_AttachProgramOptionsAdded;
			attachProgramOptionsAggregator.Completed += AttachProgramOptionsAggregator_Completed;
			attachProgramOptionsAggregator.Start();
			OnPropertyChanged(nameof(IsRefreshing));
		}

		void RemoveAggregator() {
			uiDispatcher.VerifyAccess();
			processProvider?.Dispose();
			processProvider = null;
			if (attachProgramOptionsAggregator is not null) {
				attachProgramOptionsAggregator.AttachProgramOptionsAdded -= AttachProgramOptionsAggregator_AttachProgramOptionsAdded;
				attachProgramOptionsAggregator.Completed -= AttachProgramOptionsAggregator_Completed;
				attachProgramOptionsAggregator.Dispose();
				attachProgramOptionsAggregator = null;
				OnPropertyChanged(nameof(IsRefreshing));
			}
		}

		void AttachProgramOptionsAggregator_AttachProgramOptionsAdded(object? sender, AttachProgramOptionsAddedEventArgs e) {
			uiDispatcher.VerifyAccess();
			if (attachProgramOptionsAggregator != sender)
				return;
			Debug2.Assert(processProvider is not null);
			foreach (var options in e.AttachProgramOptions) {
				if (!dbgManager.CanDebugRuntime(options.ProcessId, options.RuntimeId))
					continue;
				var vm = ProgramVM.Create(processProvider, options, attachToProcessContext);
				if (vm is null)
					continue;
				realAllItems.Add(vm);
				if (IsMatch(vm, filterText)) {
					int index = GetInsertionIndex(vm, AllItems);
					AllItems.Insert(index, vm);
				}
			}
		}

		int GetInsertionIndex(ProgramVM vm, IList<ProgramVM> list) {
			int lo = 0, hi = list.Count - 1;
			while (lo <= hi) {
				int index = (lo + hi) / 2;

				int c = Compare(vm, list[index]);
				if (c < 0)
					hi = index - 1;
				else if (c > 0)
					lo = index + 1;
				else
					return index;
			}
			return hi + 1;
		}

		void AttachProgramOptionsAggregator_Completed(object? sender, EventArgs e) {
			uiDispatcher.VerifyAccess();
			if (attachProgramOptionsAggregator != sender)
				return;
			RemoveAggregator();
		}

		void ClearAllItems() {
			uiDispatcher.VerifyAccess();
			realAllItems.Clear();
			AllItems.Reset(Array.Empty<ProgramVM>());
		}

		void FilterList(string filterText) {
			uiDispatcher.VerifyAccess();
			if (string.IsNullOrWhiteSpace(filterText))
				filterText = string.Empty;
			attachToProcessContext.SearchMatcher.SetSearchText(filterText);
			SortList(filterText);
			if (SelectedItems.Count == 0 && AllItems.Count > 0)
				SelectedItems.Add(AllItems[0]);
		}

		void SortList() {
			uiDispatcher.VerifyAccess();
			SortList(filterText);
		}
 
		void SortList(string filterText) {
			uiDispatcher.VerifyAccess();
			var newList = new List<ProgramVM>(GetFilteredItems(filterText));
			newList.Sort(this);
			AllItems.Reset(newList);
		}

		public IEnumerable<ProgramVM> Sort(IEnumerable<ProgramVM> programs) {
			uiDispatcher.VerifyAccess();
			var list = new List<ProgramVM>(programs);
			list.Sort(this);
			return list;
		}

		public int Compare([AllowNull] ProgramVM x, [AllowNull] ProgramVM y) {
			Debug.Assert(uiDispatcher.CheckAccess());
			if ((object?)x == y)
				return 0;
			if (x is null)
				return -1;
			if (y is null)
				return 1;
			var (desc, dir) = Descs.SortedColumn;

			int id;
			if (desc is null || dir == GridViewSortDirection.Default) {
				id = AttachToProcessWindowColumnIds.Default_Order;
				dir = GridViewSortDirection.Ascending;
			}
			else
				id = desc.Id;

			int diff;
			switch (id) {
			case AttachToProcessWindowColumnIds.Default_Order:
				diff = GetDefaultOrder(x, y);
				break;

			case AttachToProcessWindowColumnIds.Process:
				diff = StringComparer.OrdinalIgnoreCase.Compare(x.Name, y.Name);
				break;

			case AttachToProcessWindowColumnIds.ProcessID:
				diff = x.Id - y.Id;
				break;

			case AttachToProcessWindowColumnIds.ProcessTitle:
				diff = StringComparer.OrdinalIgnoreCase.Compare(x.Title, y.Title);
				break;

			case AttachToProcessWindowColumnIds.ProcessType:
				diff = StringComparer.OrdinalIgnoreCase.Compare(x.RuntimeName, y.RuntimeName);
				break;

			case AttachToProcessWindowColumnIds.ProcessArchitecture:
				diff = StringComparer.OrdinalIgnoreCase.Compare(x.Architecture, y.Architecture);
				break;

			case AttachToProcessWindowColumnIds.ProcessFilename:
				diff = StringComparer.OrdinalIgnoreCase.Compare(x.Filename, y.Filename);
				break;

			case AttachToProcessWindowColumnIds.ProcessCommandLine:
				diff = StringComparer.OrdinalIgnoreCase.Compare(x.CommandLine, y.CommandLine);
				break;

			default:
				throw new InvalidOperationException();
			}

			if (diff == 0 && id != AttachToProcessWindowColumnIds.Default_Order)
				diff = GetDefaultOrder(x, y);
			Debug.Assert(dir == GridViewSortDirection.Ascending || dir == GridViewSortDirection.Descending);
			if (dir == GridViewSortDirection.Descending)
				diff = -diff;
			return diff;
		}

		static int GetDefaultOrder(ProgramVM x, ProgramVM y) {
			int c = StringComparer.CurrentCultureIgnoreCase.Compare(x.Name, y.Name);
			if (c != 0)
				return c;
			c = x.Id - y.Id;
			if (c != 0)
				return c;
			return c = StringComparer.CurrentCultureIgnoreCase.Compare(x.RuntimeName, y.RuntimeName);
		}

		IEnumerable<ProgramVM> GetFilteredItems(string filterText) {
			uiDispatcher.VerifyAccess();
			foreach (var vm in realAllItems) {
				if (IsMatch(vm, filterText))
					yield return vm;
			}
		}

		bool IsMatch(ProgramVM vm, string filterText) {
			Debug.Assert(uiDispatcher.CheckAccess());
			// The order must match searchColumnDefinitions
			var allStrings = new string[] {
				GetProcess_UI(vm),
				GetPid_UI(vm),
				GetTitle_UI(vm),
				GetType_UI(vm),
				GetMachine_UI(vm),
				GetPath_UI(vm),
				GetCommandLine_UI(vm),
			};
			sbOutput.Reset();
			return attachToProcessContext.SearchMatcher.IsMatchAll(allStrings);
		}
		readonly DbgStringBuilderTextWriter sbOutput = new DbgStringBuilderTextWriter();

		string GetProcess_UI(ProgramVM vm) {
			Debug.Assert(uiDispatcher.CheckAccess());
			sbOutput.Reset();
			attachToProcessContext.Formatter.WriteProcess(sbOutput, vm);
			return sbOutput.ToString();
		}

		string GetPid_UI(ProgramVM vm) {
			Debug.Assert(uiDispatcher.CheckAccess());
			sbOutput.Reset();
			attachToProcessContext.Formatter.WritePid(sbOutput, vm);
			return sbOutput.ToString();
		}

		string GetTitle_UI(ProgramVM vm) {
			Debug.Assert(uiDispatcher.CheckAccess());
			sbOutput.Reset();
			attachToProcessContext.Formatter.WriteTitle(sbOutput, vm);
			return sbOutput.ToString();
		}

		string GetType_UI(ProgramVM vm) {
			Debug.Assert(uiDispatcher.CheckAccess());
			sbOutput.Reset();
			attachToProcessContext.Formatter.WriteType(sbOutput, vm);
			return sbOutput.ToString();
		}

		string GetMachine_UI(ProgramVM vm) {
			Debug.Assert(uiDispatcher.CheckAccess());
			sbOutput.Reset();
			attachToProcessContext.Formatter.WriteMachine(sbOutput, vm);
			return sbOutput.ToString();
		}

		string GetPath_UI(ProgramVM vm) {
			Debug.Assert(uiDispatcher.CheckAccess());
			sbOutput.Reset();
			attachToProcessContext.Formatter.WritePath(sbOutput, vm);
			return sbOutput.ToString();
		}

		string GetCommandLine_UI(ProgramVM vm) {
			Debug.Assert(uiDispatcher.CheckAccess());
			sbOutput.Reset();
			attachToProcessContext.Formatter.WriteCommandLine(sbOutput, vm);
			return sbOutput.ToString();
		}

		public void Copy(ProgramVM[] programs) {
			if (programs.Length == 0)
				return;

			var sb = new DbgStringBuilderTextWriter();
			var formatter = attachToProcessContext.Formatter;
			foreach (var vm in programs) {
				bool needTab = false;
				foreach (var column in Descs.Columns) {
					if (!column.IsVisible)
						continue;
					if (column.Name == string.Empty)
						continue;

					if (needTab)
						sb.Write(DbgTextColor.Text, "\t");
					switch (column.Id) {
					case AttachToProcessWindowColumnIds.Process:
						formatter.WriteProcess(sb, vm);
						break;

					case AttachToProcessWindowColumnIds.ProcessID:
						formatter.WritePid(sb, vm);
						break;

					case AttachToProcessWindowColumnIds.ProcessTitle:
						formatter.WriteTitle(sb, vm);
						break;

					case AttachToProcessWindowColumnIds.ProcessType:
						formatter.WriteType(sb, vm);
						break;

					case AttachToProcessWindowColumnIds.ProcessArchitecture:
						formatter.WriteMachine(sb, vm);
						break;

					case AttachToProcessWindowColumnIds.ProcessFilename:
						formatter.WritePath(sb, vm);
						break;

					case AttachToProcessWindowColumnIds.ProcessCommandLine:
						formatter.WriteCommandLine(sb, vm);
						break;

					default:
						throw new InvalidOperationException();
					}

					needTab = true;
				}
				sb.Write(DbgTextColor.Text, Environment.NewLine);
			}

			var s = sb.ToString();
			if (s.Length > 0) {
				try {
					Clipboard.SetText(s);
				}
				catch (ExternalException) { }
			}
		}

		void ShowInfoLinkPage() {
			if (infoLink is not null)
				OpenWebPage(infoLink);
		}

		static void OpenWebPage(string url) {
			try {
				Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
			}
			catch {
			}
		}

		internal void Dispose() {
			uiDispatcher.VerifyAccess();
			RemoveAggregator();
			ClearAllItems();
		}
	}
}
