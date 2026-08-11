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
using System.Linq;
using NetSpy.Contracts.Text;
using NetSpy.Scripting.Roslyn.Properties;

namespace NetSpy.Scripting.Roslyn.Common {
	sealed class HelpCommand : IScriptCommand {
		const int LEFT_COL_LEN = 20;

		static readonly (string shortcut, string help)[] keyboardShortcuts = new (string, string)[] {
			(NetSpy_Scripting_Roslyn_Resources.ShortCutKeyEnter, NetSpy_Scripting_Roslyn_Resources.HelpEnter),
			(NetSpy_Scripting_Roslyn_Resources.ShortCutKeyCtrlEnter, NetSpy_Scripting_Roslyn_Resources.HelpCtrlEnter),
			(NetSpy_Scripting_Roslyn_Resources.ShortCutKeyShiftEnter, NetSpy_Scripting_Roslyn_Resources.HelpShiftEnter),
			(NetSpy_Scripting_Roslyn_Resources.ShortCutKeyEscape, NetSpy_Scripting_Roslyn_Resources.HelpEscape),
			(NetSpy_Scripting_Roslyn_Resources.ShortCutKeyAltUp, NetSpy_Scripting_Roslyn_Resources.HelpAltUp),
			(NetSpy_Scripting_Roslyn_Resources.ShortCutKeyAltDown, NetSpy_Scripting_Roslyn_Resources.HelpAltDown),
			(NetSpy_Scripting_Roslyn_Resources.ShortCutKeyCtrlAltUp, NetSpy_Scripting_Roslyn_Resources.HelpCtrlAltUp),
			(NetSpy_Scripting_Roslyn_Resources.ShortCutKeyCtrlAltDown, NetSpy_Scripting_Roslyn_Resources.HelpCtrlAltDown),
			(NetSpy_Scripting_Roslyn_Resources.ShortCutKeyCtrlA, NetSpy_Scripting_Roslyn_Resources.HelpCtrlA),
		};
		static readonly (string directive, string help)[] scriptDirectives = new (string, string)[] {
			("#r", NetSpy_Scripting_Roslyn_Resources.HelpScriptDirective_r),
			("#load", NetSpy_Scripting_Roslyn_Resources.HelpScriptDirective_load),
		};

		public IEnumerable<string> Names {
			get { yield return "help"; }
		}

		public string ShortDescription => NetSpy_Scripting_Roslyn_Resources.HelpHelpDescription;

		public void Execute(ScriptControlVM vm, string[] args) {
			vm.ReplEditor.OutputPrintLine(NetSpy_Scripting_Roslyn_Resources.HelpKeyboardShortcuts, BoxedTextColor.ReplOutputText);
			Print(vm, keyboardShortcuts, BoxedTextColor.PreprocessorKeyword, BoxedTextColor.ReplOutputText);
			vm.ReplEditor.OutputPrintLine(NetSpy_Scripting_Roslyn_Resources.HelpReplCommands, BoxedTextColor.ReplOutputText);
			PrintCommands(vm, BoxedTextColor.PreprocessorKeyword, BoxedTextColor.ReplOutputText);
			vm.ReplEditor.OutputPrintLine(NetSpy_Scripting_Roslyn_Resources.HelpScriptDirectives, BoxedTextColor.ReplOutputText);
			Print(vm, scriptDirectives, BoxedTextColor.PreprocessorKeyword, BoxedTextColor.ReplOutputText);
		}

		void Print(ScriptControlVM vm, IEnumerable<(string cmd, string help)> descs, object color1, object color2) {
			foreach (var t in descs) {
				vm.ReplEditor.OutputPrint("  ", BoxedTextColor.ReplOutputText);
				vm.ReplEditor.OutputPrint(t.cmd, color1);
				int len = LEFT_COL_LEN - t.cmd.Length;
				if (len > 0)
					vm.ReplEditor.OutputPrint(new string(' ', len), BoxedTextColor.ReplOutputText);
				vm.ReplEditor.OutputPrint(" ", BoxedTextColor.ReplOutputText);
				vm.ReplEditor.OutputPrint(t.help, color2);
				vm.ReplEditor.OutputPrintLine(string.Empty, BoxedTextColor.ReplOutputText);
			}
		}

		void PrintCommands(ScriptControlVM vm, object color1, object color2) {
			const string CMDS_SEP = ", ";
			var hash = new HashSet<IScriptCommand>(vm.ScriptCommands);
			var cmds = hash.Select(a => (commands: a.Names.Select(b => ScriptControlVM.CMD_PREFIX + b).ToArray(), description: a.ShortDescription))
						.OrderBy(a => a.Item1[0], StringComparer.OrdinalIgnoreCase);
			foreach (var t in cmds) {
				vm.ReplEditor.OutputPrint("  ", BoxedTextColor.ReplOutputText);
				int cmdsLen = t.commands.Sum(a => a.Length) + CMDS_SEP.Length * (t.commands.Length - 1);
				for (int i = 0; i < t.commands.Length; i++) {
					if (i > 0)
						vm.ReplEditor.OutputPrint(", ", BoxedTextColor.ReplOutputText);
					vm.ReplEditor.OutputPrint(t.commands[i], color1);
				}
				int len = LEFT_COL_LEN - cmdsLen;
				if (len > 0)
					vm.ReplEditor.OutputPrint(new string(' ', len), BoxedTextColor.ReplOutputText);
				vm.ReplEditor.OutputPrint(" ", BoxedTextColor.ReplOutputText);
				vm.ReplEditor.OutputPrint(t.description, color2);
				vm.ReplEditor.OutputPrintLine(string.Empty, BoxedTextColor.ReplOutputText);
			}
		}
	}
}
