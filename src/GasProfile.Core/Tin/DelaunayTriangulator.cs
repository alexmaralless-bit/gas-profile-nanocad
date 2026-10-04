using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using GasProfile.Core.Geometry;

namespace GasProfile.Core.Tin
{
    public static class DelaunayTriangulator
    {
        public static TinSurface Build(IReadOnlyList<Point3> sourcePoints)
        {
            var points = UniqueByXY(sourcePoints);
            if (points.Count < 3)
            {
                throw new InvalidOperationException("TIN requires at least three unique points.");
            }

            var working = new List<Point3>(points);
            var superA = working.Count;
            var superB = working.Count + 1;
            var superC = working.Count + 2;
            AddSuperTriangle(working);

            var triangles = new List<TriangleIndex>
            {
                new TriangleIndex(superA, superB, superC)
            };

            for (var pointIndex = 0; pointIndex < points.Count; pointIndex++)
            {
                var point = working[pointIndex].ToPoint2();
                var badTriangles = new List<TriangleIndex>();

                foreach (var triangle in triangles)
                {
                    if (IsPointInsideCircumcircle(point, triangle, working))
                    {
                        badTriangles.Add(triangle);
                    }
                }

                var polygon = BuildBoundary(badTriangles);
                foreach (var triangle in badTriangles)
                {
                    triangles.Remove(triangle);
                }

                foreach (var edge in polygon)
                {
                    triangles.Add(new TriangleIndex(edge.A, edge.B, pointIndex));
                }
            }

            triangles.RemoveAll(t => t.A >= points.Count || t.B >= points.Count || t.C >= points.Count);
            triangles.RemoveAll(t => Math.Abs(GeometryMath.Cross(
                points[t.A].ToPoint2(),
                points[t.B].ToPoint2(),
                points[t.C].ToPoint2())) < 1e-10);

            return new TinSurface(points, triangles);
        }

        private static IReadOnlyList<Point3> UniqueByXY(IReadOnlyList<Point3> sourcePoints)
        {
            var result = new List<Point3>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var point in sourcePoints)
            {
                var key = $"{Math.Round(point.X, 6):0.000000}|{Math.Round(point.Y, 6):0.000000}";
                if (seen.Add(key))
                {
                    result.Add(point);
                }
            }

            return new ReadOnlyCollection<Point3>(result);
        }

        private static void AddSuperTriangle(List<Point3> points)
        {
            var minX = double.PositiveInfinity;
            var minY = double.PositiveInfinity;
            var maxX = double.NegativeInfinity;
            var maxY = double.NegativeInfinity;

            foreach (var point in points)
            {
                minX = Math.Min(minX, point.X);
                minY = Math.Min(minY, point.Y);
                maxX = Math.Max(maxX, point.X);
                maxY = Math.Max(maxY, point.Y);
            }

            var dx = maxX - minX;
            var dy = maxY - minY;
            var delta = Math.Max(dx, dy);
            var midX = (minX + maxX) / 2.0;
            var midY = (minY + maxY) / 2.0;
            var size = delta * 32.0;

            points.Add(new Point3(midX - size, midY - size, 0));
            points.Add(new Point3(midX, midY + size, 0));
            points.Add(new Point3(midX + size, midY - size, 0));
        }

        private static List<Edge2> BuildBoundary(IReadOnlyList<TriangleIndex> badTriangles)
        {
            var counts = new Dictionary<Edge2, int>();
            foreach (var triangle in badTriangles)
            {
                Increment(counts, triangle.EdgeAB);
                Increment(counts, triangle.EdgeBC);
                Increment(counts, triangle.EdgeCA);
            }

            var boundary = new List<Edge2>();
            foreach (var pair in counts)
            {
                if (pair.Value == 1)
                {
                    boundary.Add(pair.Key);
                }
            }

            return boundary;
        }

        private static void Increment(Dictionary<Edge2, int> counts, Edge2 edge)
        {
            counts.TryGetValue(edge, out var count);
            counts[edge] = count + 1;
        }

        private static bool IsPointInsideCircumcircle(Point2 p, TriangleIndex triangle, IReadOnlyList<Point3> points)
        {
            var a = points[triangle.A].ToPoint2();
            var b = points[triangle.B].ToPoint2();
            var c = points[triangle.C].ToPoint2();

            var ax = a.X - p.X;
            var ay = a.Y - p.Y;
            var bx = b.X - p.X;
            var by = b.Y - p.Y;
            var cx = c.X - p.X;
            var cy = c.Y - p.Y;

            var determinant =
                (ax * ax + ay * ay) * (bx * cy - cx * by) -
                (bx * bx + by * by) * (ax * cy - cx * ay) +
                (cx * cx + cy * cy) * (ax * by - bx * ay);

            var orientation = GeometryMath.Cross(a, b, c);
            return orientation > 0 ? determinant > 1e-12 : determinant < -1e-12;
        }
    }
}
