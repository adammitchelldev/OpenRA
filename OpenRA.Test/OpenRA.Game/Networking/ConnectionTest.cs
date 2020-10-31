using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using Moq;
using NUnit.Framework;
using OpenRA.Network;

namespace OpenRA.Test
{
	[TestFixture]
	public class ConnectionTest
	{
		public ConnectionTest()
		{
		}

		[TestCase(TestName = "WriteOrderPacket is writing as expected")]
		public void MemoryStreamWritingOnWriteOrderPacket()
		{
			var conn = new NetworkConnection();
			MemoryStream ms = new MemoryStream(10);

			conn.WriteOrderPacket(ms, new byte[5]);

			Assert.AreEqual(10, ms.Capacity);
			Assert.AreEqual(9, ms.Length); // 5 bytes from the array and an int
		}

		[TestCase(TestName = "WriteQueuedSyncPackets is writing and does not enlarge the buffer unless needed")]
		public void MemoryStreamWritingOnWriteQueuedSyncPacketsDoesntGrowBuffer()
		{
			var conn = new NetworkConnection();
			conn.QueuedSyncPackets.Add(new byte[5]);
			conn.QueuedSyncPackets.Add(new byte[5]);
			MemoryStream ms = new MemoryStream(18);

			conn.WriteQueuedSyncPackets(ms);

			Assert.AreEqual(18, ms.Capacity);
			Assert.AreEqual(18, ms.Length); // 5 bytes from the each pack array and an int
		}

		[TestCase(TestName = "WriteQueuedSyncPackets is writing and does enlarge buffer precisely")]
		public void MemoryStreamWritingOnWriteQueuedSyncPacketsGrowBufferExctaly()
		{
			var conn = new NetworkConnection();
			conn.QueuedSyncPackets.Add(new byte[5]);
			conn.QueuedSyncPackets.Add(new byte[5]);
			MemoryStream ms = new MemoryStream(10);

			conn.WriteQueuedSyncPackets(ms);

			Assert.AreEqual(18, ms.Capacity);
			Assert.AreEqual(18, ms.Length); // 5 bytes from the each pack array and an int
		}

		[TestCase(TestName = "Send creates the buffer precisely")]
		public void MemoryStreamWritingOnSendCreatesBufferTheRightSize()
		{
			var tcpClientMock = new Mock<ITcpClient>(MockBehavior.Loose);
			tcpClientMock.Setup(i => i.GetStream()).Returns(new MemoryStream());

			var conn = new NetworkConnection();
			conn.TcpClient = tcpClientMock.Object;
			MemoryStream ms = new MemoryStream(10);

			conn.Send(1, new List<byte[]>() { new byte[2] }, ms);

			Assert.AreEqual(10, ms.Capacity);
			Assert.AreEqual(10, ms.Length);
		}
	}
}
