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

using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;

namespace NetSpy.Text.Editor {
	sealed class EditorPositionState {
		public PositionAffinity CaretAffinity { get; }
		public int CaretVirtualSpaces { get; }
		public int CaretPosition { get; }
		public double ViewportLeft { get; }
		public int TopLinePosition { get; }
		public double TopLineVerticalDistance { get; }

		public EditorPositionState(ITextView textView) {
			CaretAffinity = textView.Caret.Position.Affinity;
			CaretVirtualSpaces = textView.Caret.Position.VirtualBufferPosition.VirtualSpaces;
			CaretPosition = textView.Caret.Position.VirtualBufferPosition.Position;
			ViewportLeft = textView.ViewportLeft;
			var line = textView.TextViewLines.FirstVisibleLine;
			TopLinePosition = line.Start;
			TopLineVerticalDistance = line.Top - textView.ViewportTop;
		}

		public EditorPositionState(PositionAffinity caretAffinity, int caretVirtualSpaces, int caretPosition, double viewportLeft, int topLinePosition, double topLineVerticalDistance) {
			CaretAffinity = caretAffinity;
			CaretVirtualSpaces = caretVirtualSpaces;
			CaretPosition = caretPosition;
			ViewportLeft = viewportLeft;
			TopLinePosition = topLinePosition;
			TopLineVerticalDistance = topLineVerticalDistance;
		}
	}
}
