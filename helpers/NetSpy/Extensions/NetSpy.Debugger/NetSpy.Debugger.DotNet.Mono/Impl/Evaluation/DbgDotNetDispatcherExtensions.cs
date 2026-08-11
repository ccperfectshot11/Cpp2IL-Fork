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
using System.Runtime.ExceptionServices;
using NetSpy.Contracts.Debugger.DotNet.Evaluation;

namespace NetSpy.Debugger.DotNet.Mono.Impl.Evaluation {
	static class DbgDotNetDispatcherExtensions {
		public static bool TryInvokeRethrow<T>(this DbgDotNetDispatcher dispatcher, Func<T> callback, out T result) {
			ExceptionDispatchInfo? exceptionInfo = null;
			bool success = dispatcher.TryInvoke(() => {
				T res2;
				try {
					res2 = callback();
				}
				catch (Exception ex) {
					exceptionInfo = ExceptionDispatchInfo.Capture(ex);
					res2 = default!;
				}
				return res2;
			}, out result);
			exceptionInfo?.Throw();
			return success;
		}
	}
}
