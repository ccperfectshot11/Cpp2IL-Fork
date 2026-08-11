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

using System.Diagnostics.CodeAnalysis;

namespace NetSpy.Contracts.Debugger.DotNet.Steppers.Engine {
	/// <summary>
	/// Frame info needed by the stepper
	/// </summary>
	public abstract class DbgDotNetEngineStepperFrameInfo {
		/// <summary>
		/// true if return values are supported
		/// </summary>
		public abstract bool SupportsReturnValues { get; }

		/// <summary>
		/// Gets the thread
		/// </summary>
		public abstract DbgThread Thread { get; }

		/// <summary>
		/// Gets the location
		/// </summary>
		/// <param name="module">Module</param>
		/// <param name="token">Method token</param>
		/// <param name="offset">IL offset</param>
		/// <returns></returns>
		public abstract bool TryGetLocation([NotNullWhen(true)] out DbgModule? module, out uint token, out uint offset);

		/// <summary>
		/// Checks if this frame is the same as another frame <paramref name="other"/>
		/// </summary>
		/// <param name="other">Other frame</param>
		/// <returns></returns>
		public abstract bool Equals(DbgDotNetEngineStepperFrameInfo other);
	}
}
