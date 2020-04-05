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
using OpenRA.Primitives;
using OpenRA.Support;

namespace OpenRA.Network
{
	public sealed class OrderManager : IDisposable
	{
		static readonly IEnumerable<Session.Client> NoClients = new Session.Client[] { };

		readonly SyncReport syncReport;
		readonly FrameData frameData = new FrameData();

		public Session LobbyInfo = new Session();
		public Session.Client LocalClient { get { return LobbyInfo.ClientWithIndex(Connection.LocalClientId); } }
		public World World;

		public readonly string Host;
		public readonly int Port;
		public readonly string Password = "";

		public string ServerError = "Server is not responding";
		public bool AuthenticationFailed = false;
		public ExternalMod ServerExternalMod = null;

		public int NetTickScale = Game.DefaultNetTickScale;
		public bool IsNetTick { get { return LocalFrameNumber % NetTickScale == 0; } }
		public int NetFrameNumber { get; private set; }
		public int LocalFrameNumber;
		public int NextReceivedNetFrame { get; private set; }
		public int NextSafeNetFrame { get; private set; }

		public int SyncFrameScale = 30; // TODO make this based on actual time, I would suggest once per second

		public int OrderLatency; // Set during lobby by a "SyncInfo" packet, see UnitOrders
		public int NextOrderFrame;
		public bool IsCatchingUp { get; private set; }

		public long LastTickTime = Game.RunTime;

		public bool GameStarted { get { return NetFrameNumber != 0; } }
		public IConnection Connection { get; private set; }

		internal int GameSaveLastFrame = -1;
		internal int GameSaveLastSyncFrame = -1;

		readonly List<Order> localOrders = new List<Order>();
		readonly List<Order> localImmediateOrders = new List<Order>();
		readonly List<Pair<int, byte[]>> receivedImmediateOrders = new List<Pair<int, byte[]>>();

		readonly List<ChatLine> chatCache = new List<ChatLine>();

		public readonly ReadOnlyList<ChatLine> ChatCache;

		bool disposed;
		bool generateSyncReport = false;

		void OutOfSync(int frame)
		{
			syncReport.DumpSyncReport(frame);
			throw new InvalidOperationException("Out of sync in frame {0}.\n Compare syncreport.log with other players.".F(frame));
		}

		public void StartGame()
		{
			if (GameStarted)
				return;

			// HACK: Need to add local client for shellmap
			frameData.AddClient(Connection.LocalClientId);
			foreach (var client in LobbyInfo.Clients)
				if (client.Index != Connection.LocalClientId)
					frameData.AddClient(client.Index);

			// Generating sync reports is expensive, so only do it if we have
			// other players to compare against if a desync did occur
			generateSyncReport = !(Connection is ReplayConnection) && LobbyInfo.GlobalSettings.EnableSyncReports;

			NetFrameNumber = 1;
			NextOrderFrame = 1;

			NetHistory.Restart(Connection.LocalClientId);

			// HACK: FramesAhead is only ever 0 in singleplayer, so we increase the rate of apparent net ticks to decrease latency
			if (OrderLatency == 0)
				NetTickScale = 1;

			// Technically redundant since we will attempt to send orders before the next frame
			// but gets our framesahead orders out sooner
			SendOrders();
		}

		public OrderManager(string host, int port, string password, IConnection conn)
		{
			Host = host;
			Port = port;
			Password = password;
			Connection = conn;
			syncReport = new SyncReport(this);
			ChatCache = new ReadOnlyList<ChatLine>(chatCache);
			AddChatLine += CacheChatLine;
		}

		public void IssueOrders(Order[] orders)
		{
			foreach (var order in orders)
				IssueOrder(order);
		}

		public void IssueOrder(Order order)
		{
			if (order.IsImmediate)
				localImmediateOrders.Add(order);
			else
				localOrders.Add(order);
		}

		public Action<string, Color, string, Color> AddChatLine = (n, nc, s, tc) => { };
		void CacheChatLine(string name, Color nameColor, string text, Color textColor)
		{
			chatCache.Add(new ChatLine(name, nameColor, text, textColor));
		}

		void SendImmediateOrders()
		{
			if (localImmediateOrders.Count != 0 && GameSaveLastFrame < NetFrameNumber)
				Connection.SendImmediate(localImmediateOrders.Select(o => o.Serialize()));
			localImmediateOrders.Clear();
		}

		void ReceiveAllOrdersAndCheckSync()
		{
			Connection.Receive(
				(clientId, packet) =>
				{
					var frame = BitConverter.ToInt32(packet, 0);
					if (packet.Length == 5 && packet[4] == (byte)OrderType.Disconnect)
						frameData.ClientQuit(clientId);
					else if (packet.Length >= 5 && packet[4] == (byte)OrderType.SyncHash)
						CheckSync(packet);
					else if (frame == 0)
						receivedImmediateOrders.Add(Pair.New(clientId, packet));
					else
						frameData.AddFrameOrders(clientId, packet);
				});
		}

		void ProcessImmediateOrders()
		{
			foreach (var p in receivedImmediateOrders)
			{
				foreach (var o in p.Second.ToOrderList(World))
				{
					UnitOrders.ProcessOrder(this, World, p.First, o);

					// A mod switch or other event has pulled the ground from beneath us
					if (disposed)
						return;
				}
			}

			receivedImmediateOrders.Clear();
		}

		Dictionary<int, byte[]> syncForFrame = new Dictionary<int, byte[]>();

		void CheckSync(byte[] packet)
		{
			var frame = BitConverter.ToInt32(packet, 0);
			byte[] existingSync;
			if (syncForFrame.TryGetValue(frame, out existingSync))
			{
				if (packet.Length != existingSync.Length)
					OutOfSync(frame);
				else
					for (var i = 0; i < packet.Length; i++)
						if (packet[i] != existingSync[i])
							OutOfSync(frame);
			}
			else
				syncForFrame.Add(frame, packet);
		}

		void CompensateForLatency()
		{
			if (Connection.LatencyReporter.IsLocal)
			{
				NextSafeNetFrame = NetFrameNumber;
				return;
			}

			var realLatency = Connection.LatencyReporter.Latency;
			var jitter = Connection.LatencyReporter.PeakJitter;

			var msPerNetFrame = World.Timestep * Game.NetTickScale;

			// Calculate catchup: we want to have enough frames in the incoming buffer to deal with peak jitter
			var targetIncomingBuffer = (int)Math.Round((double)jitter / msPerNetFrame);

			var lastAck = Connection.LastAckedFrame;

			// Our orders exist in FrameData even if they aren't acked :/
			// NextSafeNetFrame = Math.Min(lastAck - targetIncomingBuffer, NextReceivedNetFrame);
			// TODO: Add a new latency measure
			NextSafeNetFrame = NextReceivedNetFrame;

			var catchUpNetFrames = NextSafeNetFrame - NetFrameNumber;
			if (catchUpNetFrames < 0)
				catchUpNetFrames = 0;

			IsCatchingUp = catchUpNetFrames > 0;

			BufferNetHistory(realLatency, jitter, catchUpNetFrames);
		}

		IEnumerable<Session.Client> GetClientsNotReadyForNextFrame
		{
			get
			{
				return NetFrameNumber >= 1
					? frameData.ClientsNotReadyForFrame()
						.Select(a => LobbyInfo.ClientWithIndex(a))
					: NoClients;
			}
		}

		void SendOrders()
		{
			if (NetFrameNumber < 1)
				return;

			// Loop exists to ensure we never miss a frame, since that would stall the game.
			// This loop also sends the initial blank frames to get to the correct order latency.
			// while (lastFrameSent < NetFrameNumber + FramesAhead)
			// {
			// 	lastFrameSent++;
			// 	if (GameSaveLastFrame < NetFrameNumber + FramesAhead)
			// 		Connection.Send(lastFrameSent, localOrders.Select(o => o.Serialize()).ToList());
			// 	localOrders.Clear();
			// }

			if (localOrders.Count > 0)
			{
				if (GameSaveLastFrame < NextOrderFrame)
				{
					Connection.Send(NextOrderFrame, localOrders.Select(o => o.Serialize()).ToList());
					localOrders.Clear();
				}

				NextOrderFrame++;
			}
		}

		/*
		 * Only available if TickImmediate() is called first and we are ready to dispatch received orders locally.
		 * Process all incoming orders for this frame, handle sync hashes and step our net frame.
		 */
		void ProcessOrders()
		{
			var orders = frameData.OrdersForFrame(World).ToList();
			foreach (var order in orders)
			{
				UnitOrders.ProcessOrder(this, World, order.Client, order.Order);
			}

			if (NetFrameNumber % SyncFrameScale == 0)
			{
				if (NetFrameNumber >= GameSaveLastSyncFrame)
					Connection.SendSync(NetFrameNumber, OrderIO.SerializeSync(World.SyncHash()));
				else
					Connection.SendSync(NetFrameNumber, OrderIO.SerializeSync(0));

				if (generateSyncReport)
					using (new PerfSample("sync_report"))
						syncReport.UpdateSyncReport(orders);
			}

			++NetFrameNumber;
		}

		public void TickPreGame()
		{
			SendImmediateOrders();

			ReceiveAllOrdersAndCheckSync();

			Sync.RunUnsynced(Game.Settings.Debug.SyncCheckUnsyncedCode, World, ProcessImmediateOrders);
		}

		public bool TryTick()
		{
			var shouldTick = true;

			if (IsNetTick)
			{
				// Check whether or not we will be ready for a tick next frame
				// We don't need to include ourselves in the equation because we can always generate orders this frame
				shouldTick = !GetClientsNotReadyForNextFrame.Except(new[] { LocalClient }).Any();

				// Send orders only if we are currently ready, this prevents us sending orders too soon if we are
				// stalling
				if (shouldTick)
					SendOrders();
			}

			SendImmediateOrders();

			ReceiveAllOrdersAndCheckSync();

			// Always send immediate orders
			Sync.RunUnsynced(Game.Settings.Debug.SyncCheckUnsyncedCode, World, ProcessImmediateOrders);

			var willTick = shouldTick;
			if (willTick && IsNetTick)
			{
				willTick = frameData.IsReadyForFrame();
				if (willTick)
					ProcessOrders();
			}

			if (willTick)
				LocalFrameNumber++;

			return willTick;
		}

		public void BufferNetHistory(int latency, int peakJitter, int catchUpNetFrames)
		{
			var buffers = new Dictionary<int, int>();

			foreach (var c in frameData.ClientsPlayingInFrame(NetFrameNumber))
				buffers.Add(c, frameData.BufferSizeForClient(NetFrameNumber, c));

			NetHistory.Tick(new NetHistoryFrame(NetFrameNumber, NetFrameNumber <= NextSafeNetFrame, catchUpNetFrames, buffers,
				latency, Connection.LatencyReporter.Jitter, peakJitter));
		}

		public void Dispose()
		{
			disposed = true;
			if (Connection != null)
				Connection.Dispose();
		}
	}

	public class ChatLine
	{
		public readonly Color Color;
		public readonly string Name;
		public readonly string Text;
		public readonly Color TextColor;

		public ChatLine(string name, Color nameColor, string text, Color textColor)
		{
			Color = nameColor;
			Name = name;
			Text = text;
			TextColor = textColor;
		}
	}
}
