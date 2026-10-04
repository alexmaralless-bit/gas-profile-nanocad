using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using GasProfile.Core.Geometry;
using Teigha.DatabaseServices;

namespace GasProfile.NanoCad.Commands
{
    internal static class ProfileSignatures
    {
        internal static string ForRoute(Polyline polyline, double routeLength)
        {
            var builder = new StringBuilder();
            builder.Append("route|");
            builder.Append(polyline.NumberOfVertices.ToString(CultureInfo.InvariantCulture));
            builder.Append('|');
            builder.Append(routeLength.ToString("0.###", CultureInfo.InvariantCulture));

            for (var i = 0; i < polyline.NumberOfVertices; i++)
            {
                var point = polyline.GetPoint2dAt(i);
                builder.Append('|');
                builder.Append(point.X.ToString("0.###", CultureInfo.InvariantCulture));
                builder.Append(',');
                builder.Append(point.Y.ToString("0.###", CultureInfo.InvariantCulture));
            }

            return Hash(builder.ToString());
        }

        internal static string ForSurface(string layer, IReadOnlyList<Point3> points)
        {
            var builder = new StringBuilder();
            builder.Append("surface|");
            builder.Append(layer ?? string.Empty);
            builder.Append('|');
            builder.Append(points.Count.ToString(CultureInfo.InvariantCulture));

            foreach (var point in points
                .OrderBy(p => p.X)
                .ThenBy(p => p.Y)
                .ThenBy(p => p.Z))
            {
                builder.Append('|');
                builder.Append(point.X.ToString("0.###", CultureInfo.InvariantCulture));
                builder.Append(',');
                builder.Append(point.Y.ToString("0.###", CultureInfo.InvariantCulture));
                builder.Append(',');
                builder.Append(point.Z.ToString("0.###", CultureInfo.InvariantCulture));
            }

            return Hash(builder.ToString());
        }

        internal static string ForPipe(Polyline polyline)
        {
            var builder = new StringBuilder();
            builder.Append("pipe|");
            builder.Append(polyline.NumberOfVertices.ToString(CultureInfo.InvariantCulture));

            for (var i = 0; i < polyline.NumberOfVertices; i++)
            {
                var point = polyline.GetPoint2dAt(i);
                builder.Append('|');
                builder.Append(point.X.ToString("0.###", CultureInfo.InvariantCulture));
                builder.Append(',');
                builder.Append(point.Y.ToString("0.###", CultureInfo.InvariantCulture));
            }

            return Hash(builder.ToString());
        }

        private static string Hash(string text)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                var builder = new StringBuilder(bytes.Length * 2);
                foreach (var value in bytes)
                {
                    builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                }

                return builder.ToString();
            }
        }
    }
}
