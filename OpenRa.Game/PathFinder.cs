using System;
using System.Collections.Generic;
using System.Linq;
using OpenRa.FileFormats;
using OpenRa.Game.Support;

namespace OpenRa.Game
{
	class PathFinder
	{
		float[][,] passableCost = new float[4][,];
		
		public PathFinder()
		{
			for (var umt = UnitMovementType.Foot; umt <= UnitMovementType.Float; umt++)
				passableCost[(int)umt] = new float[128, 128];
			for( int x = 0 ; x < 128 ; x++ )
				for( int y = 0 ; y < 128 ; y++ )
					for (var umt = UnitMovementType.Foot; umt <= UnitMovementType.Float; umt++ )
						passableCost[(int)umt][ x, y ] = ( Rules.Map.IsInMap( x, y ) )
							? (float)TerrainCosts.Cost( umt, Rules.TileSet.GetWalkability( Rules.Map.MapTiles[ x, y ] ) )
							: float.PositiveInfinity;
		}

		bool IsBlocked(int2 from, UnitMovementType umt)
		{
			for (int v = -1; v < 2; v++)
				for (int u = -1; u < 2; u++)
					if (u != 0 || v != 0)
					{
						var p = from + new int2(u, v);
						if (passableCost[(int)umt][from.X + u, from.Y + v] < float.PositiveInfinity)
							if (Game.BuildingInfluence.CanMoveHere(p) && (Game.UnitInfluence.GetUnitAt(p) == null))
								return false;
					}
			return true;
		}

		public List<int2> FindUnitPath( int2 from, int2 target, UnitMovementType umt )
		{
			using (new PerfSample("find_unit_path"))
			{
				var pb = FindBidiPath(
					PathSearch.FromPoint(target, from, umt, false).WithCustomBlocker(AvoidUnitsNear(from, 4)),
					PathSearch.FromPoint(from, target, umt, false).WithCustomBlocker(AvoidUnitsNear(from, 4)));

				CheckSanePath2(pb, from, target);
				return pb;
			}
		}

		public List<int2> FindUnitPathToRange( int2 src, int2 target, UnitMovementType umt, int range )
		{
			using( new PerfSample( "find_unit_path_multiple_src" ) )
			{
				var tilesInRange = Game.FindTilesInCircle(target, range)
					.Where( t => Game.IsCellBuildable( t, umt ) );

				var path = FindPath( PathSearch.FromPoints( tilesInRange, src, umt, false ).WithCustomBlocker(AvoidUnitsNear(src, 4)));
				path.Reverse();
				return path;
			}
		}

		Func<int2, bool> AvoidUnitsNear(int2 p, int dist)
		{
			return q =>
				p != q &&
				((p - q).LengthSquared < dist * dist) && 
				(Game.UnitInfluence.GetUnitAt(q) != null);
		}

		public List<int2> FindPath( PathSearch search )
		{
			using (new PerfSample("find_path_inner"))
			{
				while (!search.queue.Empty)
				{
					var p = search.Expand( passableCost );
					PerfHistory.Increment("nodes_expanded", .01);

					if (search.heuristic(p) == 0)
						return MakePath(search.cellInfo, p);
				}

				// no path exists
				return new List<int2>();
			}
		}

		static List<int2> MakePath( CellInfo[ , ] cellInfo, int2 destination )
		{
			List<int2> ret = new List<int2>();
			int2 pathNode = destination;

			while( cellInfo[ pathNode.X, pathNode.Y ].Path != pathNode )
			{
				ret.Add( pathNode );
				pathNode = cellInfo[ pathNode.X, pathNode.Y ].Path;
			}

			ret.Add(pathNode);
			CheckSanePath(ret);
			return ret;
		}



		List<int2> FindBidiPath(			/* searches from both ends toward each other */
			PathSearch fromSrc,
			PathSearch fromDest)
		{
			while (!fromSrc.queue.Empty && !fromDest.queue.Empty)
			{
				/* make some progress on the first search */
				var p = fromSrc.Expand( passableCost );

				if (fromDest.cellInfo[p.X, p.Y].Seen && fromDest.cellInfo[p.X, p.Y].MinCost < float.PositiveInfinity)
					return MakeBidiPath(fromSrc, fromDest, p);

				/* make some progress on the second search */
				var q = fromDest.Expand( passableCost );

				if (fromSrc.cellInfo[q.X, q.Y].Seen && fromSrc.cellInfo[q.X, q.Y].MinCost < float.PositiveInfinity)
					return MakeBidiPath(fromSrc, fromDest, q);
			}

			return new List<int2>();
		}

		static List<int2> MakeBidiPath(PathSearch a, PathSearch b, int2 p)
		{
			var ca = a.cellInfo;
			var cb = b.cellInfo;

			var ret = new List<int2>();

			var q = p;
			while (ca[q.X, q.Y].Path != q)
			{
				ret.Add( q );
				q = ca[ q.X, q.Y ].Path;
			}
			ret.Add(q);

			ret.Reverse();

			q = p;
			while (cb[q.X, q.Y].Path != q)
			{
				q = cb[q.X, q.Y].Path;
				ret.Add(q);
			}

			CheckSanePath( ret );
			return ret;
		}



		[System.Diagnostics.Conditional( "SANITY_CHECKS" )]
		static void CheckSanePath( List<int2> path )
		{
			if( path.Count == 0 )
				return;
			var prev = path[ 0 ];
			for( int i = 0 ; i < path.Count ; i++ )
			{
				var d = path[ i ] - prev;
				if( Math.Abs( d.X ) > 1 || Math.Abs( d.Y ) > 1 )
					throw new InvalidOperationException( "(PathFinder) path sanity check failed" );
				prev = path[ i ];
			}
		}

		[System.Diagnostics.Conditional("SANITY_CHECKS")]
		static void CheckSanePath2(List<int2> path, int2 src, int2 dest)
		{
			if (path.Count == 0)
				return;

			if (path[0] != dest)
				throw new InvalidOperationException("(PathFinder) sanity check failed: doesn't go to dest");
			if (path[path.Count - 1] != src)
				throw new InvalidOperationException("(PathFinder) sanity check failed: doesn't come from src");
		}
	}

	struct CellInfo
	{
		public float MinCost;
		public int2 Path;
		public bool Seen;

		public CellInfo( float minCost, int2 path, bool seen )
		{
			MinCost = minCost;
			Path = path;
			Seen = seen;
		}
	}

	struct PathDistance : IComparable<PathDistance>
	{
		public float EstTotal;
		public int2 Location;

		public PathDistance(float estTotal, int2 location)
		{
			EstTotal = estTotal;
			Location = location;
		}

		public int CompareTo(PathDistance other)
		{
			return Math.Sign(EstTotal - other.EstTotal);
		}
	}
}
