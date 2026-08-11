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
using NetSpy.Contracts.Hex;

namespace NetSpy.Hex {
	sealed class HexVersionImpl : HexVersion {
		public override HexBuffer Buffer { get; }
		public override int VersionNumber { get; }
		public override int ReiteratedVersionNumber { get; }

		public override NormalizedHexChangeCollection? Changes => changes;
		public override HexVersion? Next => next;

		NormalizedHexChangeCollection? changes;
		HexVersion? next;

		public HexVersionImpl(HexBuffer buffer, int versionNumber, int reiteratedVersionNumber) {
			Buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
			VersionNumber = versionNumber;
			ReiteratedVersionNumber = reiteratedVersionNumber;
		}

		public HexVersionImpl SetChanges(IList<HexChange> changes, int? reiteratedVersionNumber = null) {
			var normalizedChanges = NormalizedHexChangeCollection.Create(changes);
			if (reiteratedVersionNumber is null)
				reiteratedVersionNumber = changes.Count == 0 ? ReiteratedVersionNumber : VersionNumber + 1;
			var newVersion = new HexVersionImpl(Buffer, VersionNumber + 1, reiteratedVersionNumber.Value);
			this.changes = normalizedChanges;
			next = newVersion;
			return newVersion;
		}

		public override string ToString() => $"V{VersionNumber} (r{ReiteratedVersionNumber})";
	}
}
