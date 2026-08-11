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

using System.Collections.Generic;
using System.Runtime;

namespace NetSpy.Contracts.Utilities {
	/// <summary>
	/// Helper class that calls <see cref="ProfileOptimization"/> methods
	/// </summary>
	public static class ProfileOptimizationHelper {
		// It's expected to be small so use a list
		static readonly List<string> hasInitialized = new List<string>();

		/// <summary>
		/// Starts a profile by calling <see cref="ProfileOptimization.StartProfile(string)"/>, but only if it's
		/// the first time this method has been called with the same input.
		/// </summary>
		/// <param name="type">Unique string that is used as a key to check whether we should start profiling the code</param>
		public static void StartProfile(string type) {
			lock (hasInitialized) {
				if (hasInitialized.Contains(type))
					return;
				hasInitialized.Add(type);
			}
			// ProfileOptimization.SetProfileRoot() was called when NetSpy started
			ProfileOptimization.StartProfile(type + ".profile");
		}
	}
}
