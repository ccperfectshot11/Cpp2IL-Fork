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

using System.Windows.Controls;

namespace NetSpy.AsmEditor.MethodBody {
	// There's a long delay before the Click event is triggered if the button contains a
	// TextBlock with many Inlines. The solution is to create a new button sub class and override
	// the OnClick() method.
	sealed class FastClickButton : Button {
		protected override void OnClick() {
			var cmd = Command;
			if (cmd is not null) {
				if (cmd.CanExecute(CommandParameter))
					cmd.Execute(CommandParameter);
			}
			else
				base.OnClick();
		}
	}
}
