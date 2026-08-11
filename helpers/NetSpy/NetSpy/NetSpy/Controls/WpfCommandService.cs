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
using System.Windows;
using NetSpy.Contracts.Controls;

namespace NetSpy.Controls {
	[Export(typeof(IWpfCommandService))]
	sealed class WpfCommandService : IWpfCommandService {
		readonly Dictionary<Guid, WpfCommands> toWpfCommands;

		WpfCommandService() => toWpfCommands = new Dictionary<Guid, WpfCommands>();

		public void Add(Guid guid, UIElement elem) {
			if (elem is null)
				throw new ArgumentNullException(nameof(elem));
			GetCommands(guid).Add(elem);
		}

		public void Remove(Guid guid, UIElement elem) {
			if (elem is null)
				throw new ArgumentNullException(nameof(elem));
			GetCommands(guid).Remove(elem);
		}

		IWpfCommands IWpfCommandService.GetCommands(Guid guid) => GetCommands(guid);

		WpfCommands GetCommands(Guid guid) {
			if (!toWpfCommands.TryGetValue(guid, out var c))
				toWpfCommands.Add(guid, c = new WpfCommands(guid));
			return c;
		}
	}
}
