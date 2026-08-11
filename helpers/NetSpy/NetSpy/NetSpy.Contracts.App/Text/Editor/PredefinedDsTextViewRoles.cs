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
using NetSpy.Contracts.Output;
using Microsoft.VisualStudio.Text.Editor;

namespace NetSpy.Contracts.Text.Editor {
	/// <summary>
	/// Predefined NetSpy textview roles
	/// </summary>
	public static class PredefinedDsTextViewRoles {
		/// <summary>
		/// <see cref="IDocumentViewer"/> text view role
		/// </summary>
		public const string DocumentViewer = "NetSpy-" + nameof(DocumentViewer);

		/// <summary>
		/// <see cref="ILogEditor"/> text view role
		/// </summary>
		public const string LogEditor = "NetSpy-" + nameof(LogEditor);

		/// <summary>
		/// <see cref="IOutputTextPane"/> text view role
		/// </summary>
		public const string OutputTextPane = "NetSpy-" + nameof(OutputTextPane);

		/// <summary>
		/// <see cref="IReplEditor"/> text view role
		/// </summary>
		public const string ReplEditor = "NetSpy-" + nameof(ReplEditor);

		/// <summary>
		/// Roslyn REPL (any supported language, eg. C# and Visual Basic)
		/// </summary>
		public const string RoslynRepl = "NetSpy-" + nameof(RoslynRepl);

		/// <summary>
		/// C# REPL
		/// </summary>
		public const string CSharpRepl = "NetSpy-" + nameof(CSharpRepl);

		/// <summary>
		/// Visual Basic REPL
		/// </summary>
		public const string VisualBasicRepl = "NetSpy-" + nameof(VisualBasicRepl);

		/// <summary>
		/// <see cref="ICodeEditor"/> text view role
		/// </summary>
		public const string CodeEditor = "NetSpy-" + nameof(CodeEditor);

		/// <summary>
		/// Roslyn code editor (any supported language, eg. C# and Visual Basic)
		/// </summary>
		public const string RoslynCodeEditor = "NetSpy-" + nameof(RoslynCodeEditor);

		/// <summary>
		/// Roslyn code editor (C#)
		/// </summary>
		public const string RoslynCSharpCodeEditor = "NetSpy-" + nameof(RoslynCSharpCodeEditor);

		/// <summary>
		/// Roslyn code editor (Visual Basic)
		/// </summary>
		public const string RoslynVisualBasicCodeEditor = "NetSpy-" + nameof(RoslynVisualBasicCodeEditor);

		/// <summary>
		/// Enables the custom line number margin, see <see cref="Editor.CustomLineNumberMargin"/>
		/// documentation for more info.
		/// </summary>
		public const string CustomLineNumberMargin = "NetSpy-" + nameof(CustomLineNumberMargin);

		/// <summary>
		/// <see cref="IGlyphTextMarkerService"/> services can be used. Not needed if
		/// <see cref="PredefinedTextViewRoles.Interactive"/> is already used.
		/// </summary>
		public const string CanHaveGlyphTextMarkerService = "NetSpy-" + nameof(CanHaveGlyphTextMarkerService);

		/// <summary>
		/// Allows the current line highlighter to be used. Not needed if
		/// <see cref="PredefinedTextViewRoles.Document"/> is already used.
		/// </summary>
		public const string CanHaveCurrentLineHighlighter = "NetSpy-" + nameof(CanHaveCurrentLineHighlighter);

		/// <summary>
		/// Allows the line number margin to be used. Not needed if
		/// <see cref="PredefinedTextViewRoles.Document"/> is already used.
		/// </summary>
		public const string CanHaveLineNumberMargin = "NetSpy-" + nameof(CanHaveLineNumberMargin);

		/// <summary>
		/// Allows line separators to be used. Not needed if
		/// <see cref="PredefinedTextViewRoles.Document"/> is already used.
		/// </summary>
		public const string CanHaveLineSeparator = "NetSpy-" + nameof(CanHaveLineSeparator);

		/// <summary>
		/// Allows background images to be used
		/// </summary>
		public const string CanHaveBackgroundImage = "NetSpy-" + nameof(CanHaveBackgroundImage);

		/// <summary>
		/// Allows line compressor
		/// </summary>
		public const string CanHaveLineCompressor = "NetSpy-" + nameof(CanHaveLineCompressor);

		/// <summary>
		/// Allows intellisense controllers
		/// </summary>
		public const string CanHaveIntellisenseControllers = "NetSpy-" + nameof(CanHaveIntellisenseControllers);
	}
}
