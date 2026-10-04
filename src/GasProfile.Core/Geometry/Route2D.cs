using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using GasProfile.Core.Profile;

namespace GasProfile.Core.Geometry
{
    public sealed class Route2D
    {
        private readonly double[] _vertexChainages;

        public Route2D(IReadOnlyList<Point2> vertices)
        {
            if (vertices.Count < 2)
            {
                throw new ArgumentException("Route must contain at least two vertices.", nameof(vertices));
            }

            Vertices = new ReadOnlyCollection<Point2>(new List<Point2>(vertices));
            _vertexChainages = new double[Vertices.Count];

            var length = 0.0;
            for (var i = 1; i < Vertices.Count; i++)
            {
                length += Vertices[i - 1].DistanceTo(Vertices[i]);
                _vertexChainages[i] = length;
            }

            Length = length;
        }

        public IReadOnlyList<Point2> Vertices { get; }

        public double Length { get; }

        public int SegmentCount => Vertices.Count - 1;

        public double GetVertexChainage(int vertexIndex)
        {
            return _vertexChainages[vertexIndex];
        }

        public string GetVertexPicket(int vertexIndex)
        {
            return ChainageFormatter.Format(GetVertexChainage(vertexIndex));
        }

        public Point2 GetPointAtChainage(double chainage)
        {
            if (chainage <= 0.0)
            {
                return Vertices[0];
            }

            if (chainage >= Length)
            {
                return Vertices[Vertices.Count - 1];
            }

            for (var i = 0; i < SegmentCount; i++)
            {
                var segmentStartChainage = _vertexChainages[i];
                var segmentEndChainage = _vertexChainages[i + 1];
                if (chainage > segmentEndChainage)
                {
                    continue;
                }

                var segmentLength = segmentEndChainage - segmentStartChainage;
                var t = segmentLength > 0.0
                    ? (chainage - segmentStartChainage) / segmentLength
                    : 0.0;
                var start = Vertices[i];
                var end = Vertices[i + 1];

                return new Point2(
                    start.X + (end.X - start.X) * t,
                    start.Y + (end.Y - start.Y) * t);
            }

            return Vertices[Vertices.Count - 1];
        }

        public double GetClosestChainage(Point2 point)
        {
            var bestChainage = 0.0;
            var bestDistanceSquared = double.MaxValue;

            for (var i = 0; i < SegmentCount; i++)
            {
                var start = Vertices[i];
                var end = Vertices[i + 1];
                var dx = end.X - start.X;
                var dy = end.Y - start.Y;
                var lengthSquared = dx * dx + dy * dy;
                var t = lengthSquared > 0.0
                    ? ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared
                    : 0.0;

                if (t < 0.0)
                {
                    t = 0.0;
                }
                else if (t > 1.0)
                {
                    t = 1.0;
                }

                var projectionX = start.X + dx * t;
                var projectionY = start.Y + dy * t;
                var distanceX = point.X - projectionX;
                var distanceY = point.Y - projectionY;
                var distanceSquared = distanceX * distanceX + distanceY * distanceY;

                if (distanceSquared >= bestDistanceSquared)
                {
                    continue;
                }

                bestDistanceSquared = distanceSquared;
                bestChainage = _vertexChainages[i] + Math.Sqrt(lengthSquared) * t;
            }

            return bestChainage;
        }
    }
}
