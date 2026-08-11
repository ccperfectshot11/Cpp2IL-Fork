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

using System.Windows.Input;
using NetSpy.Contracts.MVVM;

namespace NetSpy.AsmEditor.MethodBody {
	sealed class NativeMethodBodyVM : ViewModelBase {
		readonly NativeMethodBodyOptions origOptions;

		public ICommand ReinitializeCommand => new RelayCommand(a => Reinitialize());
		public UInt32VM RVA { get; }

		public NativeMethodBodyVM(NativeMethodBodyOptions options, bool initialize) {
			origOptions = options;
			RVA = new UInt32VM(a => HasErrorUpdated());

			if (initialize)
				Reinitialize();
		}

		void Reinitialize() => InitializeFrom(origOptions);
		public NativeMethodBodyOptions CreateNativeMethodBodyOptions() => CopyTo(new NativeMethodBodyOptions());
		public void InitializeFrom(NativeMethodBodyOptions options) => RVA.Value = (uint)options.RVA;

		public NativeMethodBodyOptions CopyTo(NativeMethodBodyOptions options) {
			options.RVA = (dnlib.PE.RVA)(uint)RVA.Value;
			return options;
		}

		public override bool HasError => RVA.HasError;
	}
}
