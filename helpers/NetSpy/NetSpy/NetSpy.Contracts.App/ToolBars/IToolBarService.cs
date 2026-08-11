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
using System.Windows;
using System.Windows.Controls;

namespace NetSpy.Contracts.ToolBars {
	/// <summary>
	/// ToolBar manager
	/// </summary>
	interface IToolBarService {
		/// <summary>
		/// Creates a <see cref="ToolBar"/>
		/// </summary>
		/// <param name="toolBar">The toolbar to initialize or null to create a new one and initialize it</param>
		/// <param name="toolBarGuid">Guid of toolbar, eg. <see cref="ToolBarConstants.APP_TB_GUID"/></param>
		/// <param name="commandTarget">Command target for toolbar items, eg. the owner window, or null</param>
		/// <returns></returns>
		ToolBar InitializeToolBar(ToolBar? toolBar, Guid toolBarGuid, IInputElement? commandTarget);
	}
}
