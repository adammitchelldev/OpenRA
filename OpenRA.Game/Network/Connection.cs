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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using OpenRA.Primitives;
using OpenRA.Server;

namespace OpenRA.Network
{
	public enum ConnectionState
	{
		PreConnecting,
		NotConnected,
		Connecting,
		Connected,
	}

	public interface IConnection : IDisposable
	{
		int LocalClientId { get; }
		ConnectionState ConnectionState { get; }
		IPEndPoint EndPoint { get; }
		string ErrorMessage { get; }
		void Send(int frame, IEnumerable<byte[]> orders, MemoryStream ms = null);
		void SendImmediate(IEnumerable<byte[]> orders);
		void SendSync(int frame, byte[] syncData);
		void Receive(Action<int, byte[]> packetFn);
	}

	public class ConnectionTarget
	{
		readonly DnsEndPoint[] endpoints;

		public ConnectionTarget()
		{
			endpoints = new[] { new DnsEndPoint("invalid", 0) };
		}

		public ConnectionTarget(string host, int port)
		{
			endpoints = new[] { new DnsEndPoint(host, port) };
		}

		public ConnectionTarget(IEnumerable<DnsEndPoint> endpoints)
		{
			this.endpoints = endpoints.ToArray();
			if (this.endpoints.Length == 0)
			{
				throw new ArgumentException("ConnectionTarget must have at least one address.");
			}
		}

		public IEnumerable<IPEndPoint> GetConnectEndPoints()
		{
			return endpoints
				.SelectMany(e =>
				{
					try
					{
						return Dns.GetHostAddresses(e.Host)
							.Select(a => new IPEndPoint(a, e.Port));
					}
					catch (Exception)
					{
						return Enumerable.Empty<IPEndPoint>();
					}
				})
				.ToList();
		}

		public override string ToString()
		{
			return endpoints
				.Select(e => "{0}:{1}".F(e.Host, e.Port))
				.JoinWith("/");
		}
	}

	class EchoConnection : IConnection
	{
		protected struct ReceivedPacket
		{
			public int FromClient;
			public byte[] Data;
		}

		readonly List<ReceivedPacket> receivedPackets = new List<ReceivedPacket>();
		public ReplayRecorder Recorder { get; private set; }

		public virtual int LocalClientId
		{
			get { return 1; }
		}

		public virtual ConnectionState ConnectionState
		{
			get { return ConnectionState.PreConnecting; }
		}

		public virtual IPEndPoint EndPoint
		{
			get { throw new NotSupportedException("An echo connection doesn't have an endpoint"); }
		}

		public virtual string ErrorMessage
		{
			get { return null; }
		}

		public virtual void Send(int frame, IEnumerable<byte[]> orders, MemoryStream ms = null)
		{
			ms = new MemoryStream();
			ms.WriteArray(BitConverter.GetBytes(frame));
			foreach (var o in orders)
				ms.WriteArray(o);
			Send(ms.ToArray());
		}

		public virtual void SendImmediate(IEnumerable<byte[]> orders)
		{
			foreach (var o in orders)
			{
				var ms = new MemoryStream();
				ms.WriteArray(BitConverter.GetBytes(0));
				ms.WriteArray(o);
				Send(ms.ToArray());
			}
		}

		public virtual void SendSync(int frame, byte[] syncData)
		{
			var ms = new MemoryStream(4 + syncData.Length);
			ms.WriteArray(BitConverter.GetBytes(frame));
			ms.WriteArray(syncData);
			Send(ms.GetBuffer());
		}

		protected virtual void Send(byte[] packet)
		{
			if (packet.Length == 0)
				throw new NotImplementedException();

			AddPacket(new ReceivedPacket { FromClient = LocalClientId, Data = packet });
		}

		protected void AddPacket(ReceivedPacket packet)
		{
			lock (receivedPackets)
				receivedPackets.Add(packet);
		}

		public virtual void Receive(Action<int, byte[]> packetFn)
		{
			ReceivedPacket[] packets;
			lock (receivedPackets)
			{
				packets = receivedPackets.ToArray();
				receivedPackets.Clear();
			}

			foreach (var p in packets)
			{
				packetFn(p.FromClient, p.Data);
				Recorder?.Receive(p.FromClient, p.Data);
			}
		}

		public void StartRecording(Func<string> chooseFilename)
		{
			// If we have a previous recording then save/dispose it and start a new one.
			Recorder?.Dispose();
			Recorder = new ReplayRecorder(chooseFilename);
		}

		protected virtual void Dispose(bool disposing)
		{
			if (disposing)
				Recorder?.Dispose();
		}

		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}
	}

	internal sealed class NetworkConnection : EchoConnection
	{
		readonly ConnectionTarget target;
		internal ITcpClient TcpClient;
		IPEndPoint endpoint;
		internal readonly List<byte[]> QueuedSyncPackets = new List<byte[]>();
		readonly ConcurrentQueue<byte[]> awaitingAckPackets = new ConcurrentQueue<byte[]>();
		volatile ConnectionState connectionState = ConnectionState.Connecting;
		volatile int clientId;
		bool disposed;
		string errorMessage;

		public override IPEndPoint EndPoint { get { return endpoint; } }

		public override string ErrorMessage { get { return errorMessage; } }

		public NetworkConnection(ConnectionTarget target)
		{
			this.target = target;
			new Thread(NetworkConnectionConnect)
			{
				Name = "{0} (connect to {1})".F(GetType().Name, target),
				IsBackground = true
			}.Start();
		}

		internal NetworkConnection()
		{
			// Testing constructor
		}

		void NetworkConnectionConnect()
		{
			var queue = new BlockingCollection<ITcpClient>();

			var atLeastOneEndpoint = false;
			foreach (var endpoint in target.GetConnectEndPoints())
			{
				atLeastOneEndpoint = true;
				new Thread(() =>
				{
					try
					{
						var client = new OpenRATcpClient(endpoint.AddressFamily) { NoDelay = true };
						client.Connect(endpoint.Address, endpoint.Port);

						try
						{
							queue.Add(client);
						}
						catch (InvalidOperationException)
						{
							// Another connection was faster, close this one.
							client.Close();
						}
					}
					catch (Exception ex)
					{
						errorMessage = "Failed to connect";
						Log.Write("client", "Failed to connect to {0}: {1}".F(endpoint, ex.Message));
					}
				})
				{
					Name = "{0} (connect to {1})".F(GetType().Name, endpoint),
					IsBackground = true
				}.Start();
			}

			if (!atLeastOneEndpoint)
			{
				errorMessage = "Failed to resolve address";
				connectionState = ConnectionState.NotConnected;
			}

			// Wait up to 5s for a successful connection. This should hopefully be enough because such high latency makes the game unplayable anyway.
			else if (queue.TryTake(out TcpClient, 5000))
			{
				// Copy endpoint here to have it even after getting disconnected.
				endpoint = (IPEndPoint)TcpClient.Client.RemoteEndPoint;

				new Thread(NetworkConnectionReceive)
				{
					Name = "{0} (receive from {1})".F(GetType().Name, TcpClient.Client.RemoteEndPoint),
					IsBackground = true
				}.Start();
			}
			else
			{
				connectionState = ConnectionState.NotConnected;
			}

			// Close all unneeded connections in the queue and make sure new ones are closed on the connect thread.
			queue.CompleteAdding();
			foreach (var client in queue)
				client.Close();
		}

		void NetworkConnectionReceive()
		{
			try
			{
				var reader = new BinaryReader(TcpClient.GetStream());
				var handshakeProtocol = reader.ReadInt32();

				if (handshakeProtocol != ProtocolVersion.Handshake)
					throw new InvalidOperationException(
						"Handshake protocol version mismatch. Server={0} Client={1}"
							.F(handshakeProtocol, ProtocolVersion.Handshake));

				clientId = reader.ReadInt32();
				connectionState = ConnectionState.Connected;

				while (true)
				{
					var len = reader.ReadInt32();
					var client = reader.ReadInt32();
					var buf = reader.ReadBytes(len);

					if (client == LocalClientId && len == 7 && buf[4] == (byte)OrderType.Ack)
					{
						Ack(buf);
					}
					else if (len == 0)
						throw new NotImplementedException();
					else
						AddPacket(new ReceivedPacket { FromClient = client, Data = buf });
				}
			}
			catch (Exception ex)
			{
				errorMessage = "Connection failed";
				Log.Write("client", "Connection to {0} failed: {1}".F(endpoint, ex.Message));
			}
			finally
			{
				connectionState = ConnectionState.NotConnected;
			}
		}

		void Ack(byte[] buf)
		{
			int frameReceived;
			short framesToAck;
			using (var reader = new BinaryReader(new MemoryStream(buf)))
			{
				frameReceived = reader.ReadInt32();
				reader.ReadByte();
				framesToAck = reader.ReadInt16();
			}

			using (var ms = new MemoryStream(4 + awaitingAckPackets.Take(framesToAck).Sum(i => i.Length)))
			{
				ms.WriteArray(BitConverter.GetBytes(frameReceived));

				for (int i = 0; i < framesToAck; i++)
				{
					byte[] queuedPacket = default;
					if (awaitingAckPackets.Count > 0 && !awaitingAckPackets.TryDequeue(out queuedPacket))
					{
						// The dequeing failed because of concurrency, so we retry
						for (int c = 0; c < 5; c++)
						{
							if (awaitingAckPackets.TryDequeue(out queuedPacket))
							{
								break;
							}
						}
					}

					if (queuedPacket == default)
					{
						throw new InvalidOperationException("Received acks for unknown frames");
					}

					ms.WriteArray(queuedPacket);
				}

				AddPacket(new ReceivedPacket { FromClient = LocalClientId, Data = ms.GetBuffer() });
			}
		}

		public override int LocalClientId { get { return clientId; } }
		public override ConnectionState ConnectionState { get { return connectionState; } }

		public override void SendSync(int frame, byte[] syncData)
		{
			using (var ms = new MemoryStream(4 + syncData.Length))
			{
				ms.WriteArray(BitConverter.GetBytes(frame));
				ms.WriteArray(syncData);

				QueuedSyncPackets.Add(ms.GetBuffer()); // TODO: re-add sync packets
			}
		}

		// Override send frame orders so we can hold them until ACK'ed
		public override void Send(int frame, IEnumerable<byte[]> orders, MemoryStream ms = null)
		{
			var ordersLength = orders.Sum(i => i.Length);
			var msWasNull = ms == null;
			try
			{
				if (ms == null)
				{
					ms = new MemoryStream(8 + ordersLength);
				}
				else if (ms.Capacity < 8 + ordersLength)
				{
					ms.Capacity = 8 + ordersLength;
				}

				if (orders.Count() > 0)
				{
					// Write our packet to be acked
					byte[] ackArray;
					using (var ackMs = new MemoryStream(ordersLength))
					{
						foreach (var o in orders)
						{
							ackMs.WriteArray(o);
						}

						ackArray = ackMs.GetBuffer();
					}

					awaitingAckPackets.Enqueue(ackArray); // TODO fix having to write byte buffer twice

					// Write our packet to send to the main memory stream
					ms.WriteArray(BitConverter.GetBytes(ackArray.Length + 4));
					ms.WriteArray(BitConverter.GetBytes(frame)); // TODO: Remove frames from send protocol
					ms.WriteArray(ackArray);
				}

				WriteQueuedSyncPackets(ms);
				SendNetwork(ms);
			}
			finally
			{
				if (msWasNull)
				{
					ms.Dispose();
				}
			}
		}

		protected override void Send(byte[] packet)
		{
			base.Send(packet);

			var ms = new MemoryStream();
			WriteOrderPacket(ms, packet);
			WriteQueuedSyncPackets(ms);
			SendNetwork(ms);
		}

		void SendNetwork(MemoryStream ms)
		{
			try
			{
				ms.WriteTo(TcpClient.GetStream());
			}
			catch (SocketException) { /* drop this on the floor; we'll pick up the disconnect from the reader thread */ }
			catch (ObjectDisposedException) { /* ditto */ }
			catch (InvalidOperationException) { /* ditto */ }
			catch (IOException) { /* ditto */ }
		}

		internal void WriteOrderPacket(MemoryStream ms, byte[] packet)
		{
			ms.WriteArray(BitConverter.GetBytes(packet.Length));
			ms.WriteArray(packet);
		}

		internal void WriteQueuedSyncPackets(MemoryStream ms)
		{
			if (QueuedSyncPackets.Any())
			{
				int listLengthNeeded = QueuedSyncPackets.Sum(i => 4 + i.Length);
				if (ms.Capacity - ms.Length < listLengthNeeded)
				{
					ms.Capacity += listLengthNeeded - (ms.Capacity - (int)ms.Length);
				}
			}
			else
			{
				return;
			}

			foreach (var q in QueuedSyncPackets)
			{
				ms.WriteArray(BitConverter.GetBytes(q.Length));
				ms.WriteArray(q);
				base.Send(q);
			}

			QueuedSyncPackets.Clear();
		}

		protected override void Dispose(bool disposing)
		{
			if (disposed)
				return;
			disposed = true;

			// Closing the stream will cause any reads on the receiving thread to throw.
			// This will mark the connection as no longer connected and the thread will terminate cleanly.
			TcpClient?.Close();

			base.Dispose(disposing);
		}
	}
}
