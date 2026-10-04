using System;

namespace GasProfile.Core.Geometry
{
    public static class GeometryMath
    {
        private const double Epsilon = 1e-9;

        public static double Cross(Point2 a, Point2 b, Point2 c)
        {
            return (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        }

        public static bool TryIntersectSegments(
            Point2 a,
            Point2 b,
            Point2 c,
            Point2 d,
            out Point2 intersection,
            out double tOnFirst)
        {
            intersection = default;
            tOnFirst = 0;

            var rx = b.X - a.X;
            var ry = b.Y - a.Y;
            var sx = d.X - c.X;
            var sy = d.Y - c.Y;
            var denominator = rx * sy - ry * sx;

            if (Math.Abs(denominator) < Epsilon)
            {
                return false;
            }

            var qpx = c.X - a.X;
            var qpy = c.Y - a.Y;
            var t = (qpx * sy - qpy * sx) / denominator;
            var u = (qpx * ry - qpy * rx) / denominator;

            if (t < -Epsilon || t > 1 + Epsilon || u < -Epsilon || u > 1 + Epsilon)
            {
                return false;
            }

            t = Math.Max(0, Math.Min(1, t));
            intersection = new Point2(a.X + t * rx, a.Y + t * ry);
            tOnFirst = t;
            return true;
        }
    }
}
