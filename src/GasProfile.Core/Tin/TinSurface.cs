using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using GasProfile.Core.Geometry;

namespace GasProfile.Core.Tin
{
    public sealed class TinSurface
    {
        public TinSurface(IReadOnlyList<Point3> points, IReadOnlyList<TriangleIndex> triangles)
        {
            Points = new ReadOnlyCollection<Point3>(new List<Point3>(points));
            Triangles = new ReadOnlyCollection<TriangleIndex>(new List<TriangleIndex>(triangles));
            Edges = BuildEdges(Triangles);
        }

        public IReadOnlyList<Point3> Points { get; }

        public IReadOnlyList<TriangleIndex> Triangles { get; }

        public IReadOnlyList<Edge2> Edges { get; }

        public bool TryGetElevation(Point2 point, out double z)
        {
            foreach (var triangle in Triangles)
            {
                if (TryInterpolateInTriangle(point, triangle, out z))
                {
                    return true;
                }
            }

            z = 0;
            return false;
        }

        private bool TryInterpolateInTriangle(Point2 p, TriangleIndex triangle, out double z)
        {
            var a = Points[triangle.A];
            var b = Points[triangle.B];
            var c = Points[triangle.C];

            var ax = a.X;
            var ay = a.Y;
            var bx = b.X;
            var by = b.Y;
            var cx = c.X;
            var cy = c.Y;

            var denominator = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
            if (Math.Abs(denominator) < 1e-12)
            {
                z = 0;
                return false;
            }

            var w1 = ((by - cy) * (p.X - cx) + (cx - bx) * (p.Y - cy)) / denominator;
            var w2 = ((cy - ay) * (p.X - cx) + (ax - cx) * (p.Y - cy)) / denominator;
            var w3 = 1.0 - w1 - w2;

            if (w1 < -1e-8 || w2 < -1e-8 || w3 < -1e-8)
            {
                z = 0;
                return false;
            }

            z = w1 * a.Z + w2 * b.Z + w3 * c.Z;
            return true;
        }

        private static IReadOnlyList<Edge2> BuildEdges(IReadOnlyList<TriangleIndex> triangles)
        {
            var edges = new HashSet<Edge2>();
            foreach (var triangle in triangles)
            {
                edges.Add(triangle.EdgeAB);
                edges.Add(triangle.EdgeBC);
                edges.Add(triangle.EdgeCA);
            }

            return new ReadOnlyCollection<Edge2>(new List<Edge2>(edges));
        }
    }
}
