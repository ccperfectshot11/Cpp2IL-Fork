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
using dnlib.DotNet;
using NetSpy.AsmEditor.ViewHelpers;
using NetSpy.Contracts.App;
using NetSpy.Contracts.AsmEditor.Compiler;
using NetSpy.Contracts.Decompiler;
using NetSpy.Contracts.Images;
using NetSpy.Contracts.MVVM;

namespace NetSpy.AsmEditor.Compiler {
	readonly struct EditCodeVMOptions {
		public readonly RawModuleBytesProvider RawModuleBytesProvider;
		public readonly IOpenFromGAC OpenFromGAC;
		public readonly IOpenAssembly OpenAssembly;
		public readonly IPickFilename PickFilename;
		public readonly ILanguageCompiler LanguageCompiler;
		public readonly IDecompiler Decompiler;
		public readonly ModuleDef SourceModule;
		public readonly ImageReference AddDocumentsImage;

		public EditCodeVMOptions(RawModuleBytesProvider rawModuleBytesProvider, IOpenFromGAC openFromGAC, IOpenAssembly openAssembly, IPickFilename pickFilename, ILanguageCompiler languageCompiler, IDecompiler decompiler, ModuleDef sourceModule, ImageReference addDocumentsImage) {
			RawModuleBytesProvider = rawModuleBytesProvider ?? throw new ArgumentNullException(nameof(rawModuleBytesProvider));
			OpenFromGAC = openFromGAC ?? throw new ArgumentNullException(nameof(openFromGAC));
			OpenAssembly = openAssembly ?? throw new ArgumentNullException(nameof(openAssembly));
			PickFilename = pickFilename ?? throw new ArgumentNullException(nameof(pickFilename));
			LanguageCompiler = languageCompiler ?? throw new ArgumentNullException(nameof(languageCompiler));
			Decompiler = decompiler ?? throw new ArgumentNullException(nameof(decompiler));
			SourceModule = sourceModule ?? throw new ArgumentNullException(nameof(sourceModule));
			AddDocumentsImage = addDocumentsImage;
		}
	}
}
