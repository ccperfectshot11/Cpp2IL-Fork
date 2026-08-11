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
using NetSpy.Contracts.Hex;
using NetSpy.Contracts.MVVM;
using NetSpy.Properties;

namespace NetSpy.Hex.Commands {
	/// <summary>
	/// <see cref="HexPosition"/>
	/// </summary>
	sealed class HexPositionVM : NumberDataFieldVM<HexPosition, HexPosition> {
		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="onUpdated">Called when value gets updated</param>
		/// <param name="useDecimal">true to use decimal, false to use hex, or null if it depends on the value</param>
		public HexPositionVM(Action<DataFieldVM> onUpdated, bool? useDecimal = null)
			: this(0, onUpdated, useDecimal) {
		}

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="value">Initial value</param>
		/// <param name="onUpdated">Called when value gets updated</param>
		/// <param name="useDecimal">true to use decimal, false to use hex, or null if it depends on the value</param>
		public HexPositionVM(HexPosition value, Action<DataFieldVM> onUpdated, bool? useDecimal = null)
			: base(onUpdated, HexPosition.Zero, HexPosition.MaxEndPosition, useDecimal) => SetValueFromConstructor(value);

		/// <inheritdoc/>
		protected override string OnNewValue(HexPosition value) {
			if (!IsValid(value))
				return NetSpy_Resources.InvalidValue;
			return value.ToString();
		}

		/// <inheritdoc/>
		protected override string? ConvertToValue(out HexPosition value) {
			if (HexPosition.TryParse(StringValue, out value)) {
				if (!IsValid(value))
					return string.Format(NetSpy_Resources.ValueMustBeWithinRange, Min, Max);
				return null;
			}
			return NetSpy_Resources.InvalidValue;
		}

		bool IsValid(HexPosition position) {
			if (Min <= Max)
				return Min <= position && position <= Max;
			return (position >= Min && position < HexPosition.MaxEndPosition) || position <= Max;
		}
	}
}
