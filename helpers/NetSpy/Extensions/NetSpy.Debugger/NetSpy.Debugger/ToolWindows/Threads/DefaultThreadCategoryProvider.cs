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

using NetSpy.Contracts.Debugger;
using NetSpy.Contracts.Images;
using NetSpy.Debugger.Properties;

namespace NetSpy.Debugger.ToolWindows.Threads {
	[ExportThreadCategoryProvider]
	sealed class DefaultThreadCategoryProvider : ThreadCategoryProvider {
		public override ThreadCategoryInfo? GetCategory(string kind) {
			switch (kind) {
			case PredefinedThreadKinds.Unknown:
				return new ThreadCategoryInfo(DsImages.QuestionMark, NetSpy_Debugger_Resources.ThreadType_Unknown);
			case PredefinedThreadKinds.Main:
				return new ThreadCategoryInfo(DsImages.Thread, NetSpy_Debugger_Resources.ThreadType_Main);
			case PredefinedThreadKinds.ThreadPool:
				return new ThreadCategoryInfo(DsImages.Process, NetSpy_Debugger_Resources.ThreadType_ThreadPool);
			case PredefinedThreadKinds.WorkerThread:
				return new ThreadCategoryInfo(DsImages.Process, NetSpy_Debugger_Resources.ThreadType_Worker);
			case PredefinedThreadKinds.Terminated:
				return new ThreadCategoryInfo(DsImages.QuestionMark, NetSpy_Debugger_Resources.ThreadType_Terminated);
			case PredefinedThreadKinds.GC:
				return new ThreadCategoryInfo(DsImages.Process, "GC");// No need to localize it
			case PredefinedThreadKinds.Finalizer:
				return new ThreadCategoryInfo(DsImages.Process, "Finalizer");// No need to localize it
			}
			return null;
		}
	}
}
