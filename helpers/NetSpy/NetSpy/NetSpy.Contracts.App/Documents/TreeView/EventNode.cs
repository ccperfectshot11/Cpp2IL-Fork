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
using NetSpy.Contracts.TreeView;

namespace NetSpy.Contracts.Documents.TreeView {
	/// <summary>
	/// An event node
	/// </summary>
	public abstract class EventNode : DocumentTreeNodeData, IMDTokenNode {
		/// <summary>
		/// Gets the event
		/// </summary>
		public EventDef EventDef { get; }

		IMDTokenProvider? IMDTokenNode.Reference => EventDef;

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="event">Event</param>
		protected EventNode(EventDef @event) => EventDef = @event ?? throw new ArgumentNullException(nameof(@event));

		/// <summary>
		/// Creates a <see cref="MethodNode"/>, an adder, remover, invoker, or an other method
		/// </summary>
		/// <param name="method">Method</param>
		/// <returns></returns>
		public MethodNode Create(MethodDef method) => Context.DocumentTreeView.CreateEvent(method);
	}
}
