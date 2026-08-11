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

using System.Windows;

namespace NetSpy.Contracts.Command {
	/// <summary>
	/// Converts raw input to commands and sends them to <see cref="ICommandTarget"/>s
	/// </summary>
	public interface ICommandService {
		/// <summary>
		/// Registers an element
		/// </summary>
		/// <param name="sourceElement">Source element that provides the keyboard input</param>
		/// <param name="target">Target object</param>
		/// <returns></returns>
		IRegisteredCommandElement Register(UIElement sourceElement, object target);
	}
}
