using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace OpenRA.Server
{
	public class ServerGame
	{
		const int JankThreshold = 250;

		Stopwatch gameTimer;
		public long RunTime
		{
			get { return gameTimer.ElapsedMilliseconds; }
		}

		public readonly OrderBuffer OrderBuffer;
		public int CurrentNetFrame { get; protected set; }
		public long NextFrameTick { get; protected set; }
		public int NetTimestep { get; protected set; }

		int slowdownHold;
		int slowdownAmount;
		public int AdjustedTimestep { get { return NetTimestep + slowdownAmount; } }

		public int MillisToNextNetFrame
		{
			get { return (int)(NextFrameTick - RunTime); }
			set { NextFrameTick = RunTime + value; }
		}

		public ServerGame(int worldTimeStep)
		{
			CurrentNetFrame = 1;
			NetTimestep = worldTimeStep * Game.DefaultNetTickScale; // TODO: Set net tick scale via lobby settings
			NextFrameTick = NetTimestep;
			gameTimer = Stopwatch.StartNew();
			OrderBuffer = new OrderBuffer();
		}

		public void TryTick(IFrameOrderDispatcher dispatcher)
		{
			var now = RunTime;
			if (now >= NextFrameTick)
			{
				OrderBuffer.DispatchOrders(dispatcher);

				CurrentNetFrame++;
				if (now - NextFrameTick > JankThreshold)
					NextFrameTick = now + AdjustedTimestep;
				else
					NextFrameTick += AdjustedTimestep;

				if (slowdownHold > 0)
					slowdownHold--;

				if (slowdownHold == 0 && slowdownAmount > 0)
					slowdownAmount = slowdownAmount - (slowdownAmount / 4) - 1;
			}
		}

		Dictionary<int, int> slowdowns = new Dictionary<int, int>();

		public void SlowDown(int amount)
		{
			if (slowdownAmount < amount)
			{
				slowdownAmount = amount;
				slowdownHold = amount;
			}
		}
	}
}
