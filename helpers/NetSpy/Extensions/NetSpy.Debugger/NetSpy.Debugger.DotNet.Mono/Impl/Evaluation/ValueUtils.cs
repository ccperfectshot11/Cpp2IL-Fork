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
using System.Linq;
using NetSpy.Debugger.DotNet.Metadata;
using Mono.Debugger.Soft;

namespace NetSpy.Debugger.DotNet.Mono.Impl.Evaluation {
	static class ValueUtils {
		public static Value Unbox(ObjectMirror obj, DmdType objType) {
			if (!objType.IsEnum) {
				switch (DmdType.GetTypeCode(objType)) {
				case TypeCode.Boolean:
				case TypeCode.Char:
				case TypeCode.SByte:
				case TypeCode.Byte:
				case TypeCode.Int16:
				case TypeCode.UInt16:
				case TypeCode.Int32:
				case TypeCode.UInt32:
				case TypeCode.Int64:
				case TypeCode.UInt64:
				case TypeCode.Single:
				case TypeCode.Double:
					return GetFieldValues(obj).Single();
				}
			}
			if (objType.IsEnum) {
				var value = (PrimitiveValue)GetFieldValues(obj).Single();
				return obj.VirtualMachine.CreateEnumMirror(obj.Type, value);
			}
			else {
				var values = GetFieldValues(obj).ToArray();
				return obj.VirtualMachine.CreateStructMirror(obj.Type, values);
			}
		}

		static IEnumerable<Value> GetFieldValues(ObjectMirror obj) {
			var allFields = obj.Type.GetFields().Where(a => !a.IsStatic && !a.IsLiteral).ToArray();
			foreach (var v in obj.GetValues(allFields))
				yield return v;
		}

		public static Value MakePrimitiveValueIfPossible(Value value, DmdType type) {
			if (!type.IsValueType)
				return value;
			if (type.IsEnum)
				return value;
			var sm = value as StructMirror;
			if (sm is null)
				return value;
			switch (DmdType.GetTypeCode(type)) {
			case TypeCode.Boolean:
			case TypeCode.Char:
			case TypeCode.SByte:
			case TypeCode.Byte:
			case TypeCode.Int16:
			case TypeCode.UInt16:
			case TypeCode.Int32:
			case TypeCode.UInt32:
			case TypeCode.Int64:
			case TypeCode.UInt64:
			case TypeCode.Single:
			case TypeCode.Double:
				if (sm.Fields.Length != 1)
					throw new InvalidOperationException();
				return sm.Fields[0];
			}
			return value;
		}
	}
}
