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
using NetSpy.Contracts.Debugger;
using NetSpy.Contracts.Debugger.CallStack;
using NetSpy.Contracts.Debugger.Code;

namespace NetSpy.Debugger.ToolWindows.Threads {
	static class ThreadUtilities {
		/// <summary>
		/// Gets the first non-null frame location. This location must be <see cref="DbgCodeLocation.Close"/>'d
		/// by the caller.
		/// </summary>
		/// <param name="thread">Thread</param>
		/// <returns></returns>
		public static (DbgCodeLocation? location, int frameIndex) GetFirstFrameLocation(DbgThread thread) {
			DbgStackWalker? stackWalker = null;
			var objsToFree = new List<DbgObject>();
			try {
				stackWalker = thread.CreateStackWalker();
				objsToFree.Add(stackWalker);
				int frameIndex = 0;
				while (frameIndex < 20) {
					// Usually the first frame contains a location and if not, the one after that.
					var frames = stackWalker.GetNextStackFrames(2);
					objsToFree.AddRange(frames);
					if (frames.Length == 0)
						break;
					foreach (var frame in frames) {
						var location = frame.Location;
						if (location is not null)
							return (location.Clone(), frameIndex);
						frameIndex++;
					}
				}
			}
			finally {
				if (objsToFree.Count > 0)
					thread.Process.DbgManager.Close(objsToFree);
			}
			return (null, -1);
		}
	}
}
