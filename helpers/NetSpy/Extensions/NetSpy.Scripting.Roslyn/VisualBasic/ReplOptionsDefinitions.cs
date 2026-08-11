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

using NetSpy.Contracts.Settings.Dialog;
using NetSpy.Contracts.Settings.Repl;
using NetSpy.Contracts.Text;

namespace NetSpy.Scripting.Roslyn.VisualBasic {
	static class ReplOptionsDefinitions {
#pragma warning disable CS0169
		[ExportReplOptionsDefinition("Visual Basic", ContentTypes.ReplVisualBasicRoslyn, AppSettingsConstants.GUID_REPL_VISUAL_BASIC_ROSLYN)]
		static readonly ReplOptionsDefinition? visualBasicReplOptionsDefinition;
#pragma warning restore CS0169
	}
}
