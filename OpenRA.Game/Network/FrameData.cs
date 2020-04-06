#region Copyright & License Information
/*
 * Copyright 2007-2020 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenRA.Network
{
	class FrameData
	{
		public struct ClientOrder
		{
			public int Client;
			public Order Order;

			public override string ToString()
			{
				return "ClientId: {0} {1}".F(Client, Order);
			}
		}

		readonly HashSet<int> quitClients = new HashSet<int>();
		readonly Dictionary<int, Queue<byte[]>> framePackets = new Dictionary<int, Queue<byte[]>>();

		public IEnumerable<int> ClientsPlayingInFrame()
		{
			return framePackets.Keys.Where(x => !quitClients.Contains(x)).OrderBy(x => x);
		}

		public void AddClient(int clientId)
		{
			if (!framePackets.ContainsKey(clientId))
				framePackets.Add(clientId, new Queue<byte[]>());
		}

		public void ClientQuit(int clientId)
		{
			quitClients.Add(clientId);
		}

		public void AddFrameOrders(int clientId, byte[] orders)
		{
			if (!framePackets.ContainsKey(clientId))
				throw new InvalidOperationException("Client must be added before submitting orders");

			var frameData = framePackets[clientId];
			frameData.Enqueue(orders);
		}

		public bool IsReadyForFrame()
		{
			return !ClientsNotReadyForFrame().Any();
		}

		public IEnumerable<int> ClientsNotReadyForFrame()
		{
			return ClientsPlayingInFrame()
				.Where(client => framePackets[client].Count == 0);
		}

		public IEnumerable<ClientOrder> OrdersForFrame(World world)
		{
			return ClientsPlayingInFrame()
				.SelectMany(x => framePackets[x].Dequeue().ToOrderList(world)
					.Select(y => new ClientOrder { Client = x, Order = y }));
		}

		public int BufferSizeForClient(int client)
		{
			return framePackets[client].Count;
		}
	}
}
