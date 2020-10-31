using System;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace OpenRA
{
	public interface ITcpClient : IDisposable
	{
		Stream GetStream();
		EndPoint RemoteEndPoint { get; }
		void Close();
		Socket Client { get; }
		bool NoDelay { get; set; }
		void Connect(IPAddress iPAddress, int port);
	}

	public class OpenRATcpClient : ITcpClient
	{
		internal TcpClient BackingClient;

		public EndPoint RemoteEndPoint => throw new NotImplementedException();

		public OpenRATcpClient(AddressFamily addressFamily)
		{
			BackingClient = new TcpClient(addressFamily);
		}

		public void Close()
		{
			BackingClient.Close();
		}

		public void Dispose()
		{
			BackingClient.Dispose();
		}

		public Stream GetStream()
		{
			return BackingClient.GetStream();
		}

		public Socket Client
		{
			get
			{
				return BackingClient.Client;
			}
		}

		public bool NoDelay
		{
			get
			{
				return BackingClient.NoDelay;
			}
			set
			{
				BackingClient.NoDelay = value;
			}
		}

		public void Connect(IPAddress iPAddress, int port)
		{
			BackingClient.Connect(iPAddress, port);
		}
	}
}
