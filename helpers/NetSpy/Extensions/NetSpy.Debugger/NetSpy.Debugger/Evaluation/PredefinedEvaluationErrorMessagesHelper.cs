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
using System.Diagnostics;
using NetSpy.Contracts.Debugger.Engine.Evaluation;
using NetSpy.Contracts.Debugger.Engine.Evaluation.Internal;
using NetSpy.Debugger.Properties;

namespace NetSpy.Debugger.Evaluation {
	static class PredefinedEvaluationErrorMessagesHelper {
		static readonly Dictionary<string, string> toErrorMessage;
		static PredefinedEvaluationErrorMessagesHelper() {
			const int TOTAL_COUNT = 12;
			toErrorMessage = new Dictionary<string, string>(TOTAL_COUNT, StringComparer.Ordinal) {
				{ PredefinedEvaluationErrorMessages.InternalDebuggerError, NetSpy_Debugger_Resources.InternalDebuggerError },
				{ PredefinedEvaluationErrorMessages.ExpressionCausesSideEffects, NetSpy_Debugger_Resources.ExpressionCausesSideEffectsNoEval },
				{ PredefinedEvaluationErrorMessages.FuncEvalDisabled, NetSpy_Debugger_Resources.FunctionEvaluationDisabled },
				{ PredefinedEvaluationErrorMessages.FuncEvalTimedOut, NetSpy_Debugger_Resources.Locals_Error_EvaluationTimedOut },
				{ PredefinedEvaluationErrorMessages.FuncEvalTimedOutNowDisabled, NetSpy_Debugger_Resources.Locals_Error_EvalTimedOutIsDisabled },
				{ PredefinedEvaluationErrorMessages.CanFuncEvalOnlyWhenPaused, NetSpy_Debugger_Resources.Error_CantEvalUnlessDebuggerStopped },
				{ PredefinedEvaluationErrorMessages.CantFuncEvalWhenUnhandledExceptionHasOccurred, NetSpy_Debugger_Resources.Error_CantEvalWhenUnhandledExceptionHasOccurred },
				{ PredefinedEvaluationErrorMessages.CantFuncEval, NetSpy_Debugger_Resources.Locals_Error_EvalDisabledCantCallPropsAndMethods },
				{ PredefinedEvaluationErrorMessages.CantFuncEvaluateWhenThreadIsAtUnsafePoint, NetSpy_Debugger_Resources.Locals_Error_CantEvaluateWhenThreadIsAtUnsafePoint },
				{ PredefinedEvaluationErrorMessages.FuncEvalRequiresAllThreadsToRun, NetSpy_Debugger_Resources.FuncEvalRequiresAllThreadsToRun },
				{ PredefinedEvaluationErrorMessages.CannotReadLocalOrArgumentMaybeOptimizedAway, NetSpy_Debugger_Resources.CannotReadLocalOrArgumentMaybeOptimizedAway },
				{ PredefinedEvaluationErrorMessages.RuntimeIsUnableToEvaluateExpression, NetSpy_Debugger_Resources.RuntimeIsUnableToEvaluateExpression },
			};
			Debug.Assert(toErrorMessage.Count == TOTAL_COUNT);
		}

		public static string GetErrorMessage(string error) {
			if (toErrorMessage.TryGetValue(error, out var msg))
				return msg;
			return error;
		}

		public static string? GetErrorMessageOrNull(string? error) {
			if (error is null)
				return null;
			return GetErrorMessage(error);
		}
	}

	[Export(typeof(IPredefinedEvaluationErrorMessagesHelper))]
	sealed class PredefinedEvaluationErrorMessagesHelperImpl : IPredefinedEvaluationErrorMessagesHelper {
		public string GetErrorMessage(string error) {
			if (error is null)
				throw new ArgumentNullException(nameof(error));
			return PredefinedEvaluationErrorMessagesHelper.GetErrorMessage(error);
		}
	}
}
