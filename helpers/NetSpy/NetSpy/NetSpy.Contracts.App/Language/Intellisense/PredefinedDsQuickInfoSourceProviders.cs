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

using NetSpy.Contracts.Documents.Tabs.DocViewer;
using Microsoft.VisualStudio.Language.Intellisense;

namespace NetSpy.Contracts.Language.Intellisense {
	/// <summary>
	/// Predefined <see cref="IQuickInfoSourceProvider"/> names
	/// </summary>
	public static class PredefinedDsQuickInfoSourceProviders {
		/// <summary>
		/// <see cref="IDocumentViewer"/>
		/// </summary>
		public const string DocumentViewer = "NetSpy-" + nameof(DocumentViewer);

		/// <summary>
		/// Roslyn languages (C# or Visual Basic)
		/// </summary>
		public const string Roslyn = "NetSpy-" + nameof(Roslyn);

		/// <summary>
		/// URI
		/// </summary>
		public const string Uri = "NetSpy-" + nameof(Uri);
	}
}
