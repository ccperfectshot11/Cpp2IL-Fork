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
using System.Linq;
using NetSpy.Contracts.App;

namespace NetSpy.Themes {
	[Export(typeof(IAppCommandLineArgsHandler))]
	sealed class AppCommandLineArgsHandler : IAppCommandLineArgsHandler {
		readonly IThemeServiceImpl themeService;

		[ImportingConstructor]
		AppCommandLineArgsHandler(IThemeServiceImpl themeService) => this.themeService = themeService;

		public double Order => 0;

		public void OnNewArgs(IAppCommandLineArgs args) {
			if (string.IsNullOrEmpty(args.Theme))
				return;

			bool isGuid = Guid.TryParse(args.Theme, out var guid);
			var theme = themeService.AllThemes.FirstOrDefault(a => isGuid ? a.Guid == guid : !string.IsNullOrEmpty(a.Name) && StringComparer.InvariantCulture.Equals(a.Name, args.Theme));
			if (theme is not null)
				themeService.Theme = theme;
		}
	}
}
