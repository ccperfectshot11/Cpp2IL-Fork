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
using System.Linq;
using NetSpy.Text.MEF;
using Microsoft.VisualStudio.Text.Editor;

namespace NetSpy.Text.Editor {
	[Export(typeof(IWpfTextViewConnectionListenerServiceProvider))]
	sealed class WpfTextViewConnectionListenerServiceProvider : IWpfTextViewConnectionListenerServiceProvider {
		readonly Lazy<IWpfTextViewConnectionListener, IContentTypeAndTextViewRoleMetadata>[] wpfTextViewConnectionListeners;
		readonly Lazy<ITextViewConnectionListener, IContentTypeAndTextViewRoleMetadata>[] textViewConnectionListeners;

		[ImportingConstructor]
		WpfTextViewConnectionListenerServiceProvider([ImportMany] IEnumerable<Lazy<IWpfTextViewConnectionListener, IContentTypeAndTextViewRoleMetadata>> wpfTextViewConnectionListeners, [ImportMany] IEnumerable<Lazy<ITextViewConnectionListener, IContentTypeAndTextViewRoleMetadata>> textViewConnectionListeners) {
			this.wpfTextViewConnectionListeners = wpfTextViewConnectionListeners.ToArray();
			this.textViewConnectionListeners = textViewConnectionListeners.ToArray();
		}

		public void Create(IWpfTextView wpfTextView) {
			if (wpfTextView is null)
				throw new ArgumentNullException(nameof(wpfTextView));
			new WpfTextViewConnectionListenerService(wpfTextView, wpfTextViewConnectionListeners, textViewConnectionListeners);
		}
	}
}
