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

using System.Globalization;
using NetSpy.Contracts.Debugger.DotNet.Evaluation;
using NetSpy.Contracts.Debugger.DotNet.Evaluation.Formatters;
using NetSpy.Contracts.Debugger.Evaluation;
using NetSpy.Contracts.Debugger.Text;
using NetSpy.Debugger.DotNet.Metadata;

namespace NetSpy.Roslyn.Debugger.Formatters {
	abstract class LanguageFormatter : DbgDotNetFormatter {
		public override void FormatType(DbgEvaluationInfo evalInfo, IDbgTextWriter output, DmdType type,DbgDotNetValue? value, DbgValueFormatterTypeOptions options, CultureInfo? cultureInfo) =>
			FormatType(evalInfo, output, type, default, value, options, cultureInfo);

		public abstract void FormatType(DbgEvaluationInfo evalInfo, IDbgTextWriter output, DmdType type, AdditionalTypeInfoState additionalTypeInfo, DbgDotNetValue? value, DbgValueFormatterTypeOptions options, CultureInfo? cultureInfo);

		public override void FormatExceptionName(DbgEvaluationContext context, IDbgTextWriter output, uint id) =>
			output.Write(DbgTextColor.ExceptionName, AliasConstants.ExceptionName);

		public override void FormatStowedExceptionName(DbgEvaluationContext context, IDbgTextWriter output, uint id) =>
			output.Write(DbgTextColor.StowedExceptionName, AliasConstants.StowedExceptionName);

		public override void FormatReturnValueName(DbgEvaluationContext context, IDbgTextWriter output, uint id) {
			if (id == 0)
				output.Write(DbgTextColor.ReturnValueName, AliasConstants.ReturnValueName);
			else
				output.Write(DbgTextColor.ReturnValueName, AliasConstants.ReturnValueName + id.ToString());
		}

		public override void FormatObjectIdName(DbgEvaluationContext context, IDbgTextWriter output, uint id) =>
			output.Write(DbgTextColor.ObjectIdName, AliasConstants.ObjectIdName + id.ToString());
	}
}
