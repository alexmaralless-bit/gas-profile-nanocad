using System;
using System.Collections.Generic;
using GasProfile.Core.Geometry;
using GasProfile.Core.Tin;

namespace GasProfile.Core.Profile
{
    public static class ProfileBuilder
    {
        public static IReadOnlyList<ProfilePoint> BuildGroundProfile(Route2D route, TinSurface tin)
        {
            var points = new List<ProfilePoint>();

            for (var i = 0; i < route.Vertices.Count; i++)
            {
                var type = i == 0
                    ? ProfilePointType.Start
                    : i == route.Vertices.Count - 1
                        ? ProfilePointType.End
                        : ProfilePointType.RouteVertex;

                points.Add(CreatePoint(route.GetVertexChainage(i), route.Vertices[i], type, tin));
            }

            AddTinEdgeIntersections(route, tin, points);

            points.Sort((a, b) => a.Chainage.CompareTo(b.Chainage));
            return RemoveNearDuplicates(points);
        }

        private static void AddTinEdgeIntersections(Route2D route, TinSurface tin, List<ProfilePoint> points)
        {
            for (var segmentIndex = 0; segmentIndex < route.SegmentCount; segmentIndex++)
            {
                var routeA = route.Vertices[segmentIndex];
                var routeB = route.Vertices[segmentIndex + 1];
                var segmentStartChainage = route.GetVertexChainage(segmentIndex);
                var segmentLength = routeA.DistanceTo(routeB);

                foreach (var edge in tin.Edges)
                {
                    var edgeA = tin.Points[edge.A].ToPoint2();
                    var edgeB = tin.Points[edge.B].ToPoint2();
                    if (!GeometryMath.TryIntersectSegments(routeA, routeB, edgeA, edgeB, out var intersection, out var t))
                    {
                        continue;
                    }

                    var chainage = segmentStartChainage + t * segmentLength;
                    points.Add(CreatePoint(chainage, intersection, ProfilePointType.TinEdge, tin));
                }
            }
        }

        private static ProfilePoint CreatePoint(double chainage, Point2 planPoint, ProfilePointType type, TinSurface tin)
        {
            return new ProfilePoint(
                chainage,
                planPoint,
                type,
                tin.TryGetElevation(planPoint, out var z) ? z : null);
        }

        private static IReadOnlyList<ProfilePoint> RemoveNearDuplicates(IReadOnlyList<ProfilePoint> sortedPoints)
        {
            var result = new List<ProfilePoint>();
            foreach (var point in sortedPoints)
            {
                if (result.Count > 0 && Math.Abs(result[result.Count - 1].Chainage - point.Chainage) < 0.001)
                {
                    continue;
                }

                result.Add(point);
            }

            return result;
        }
    }
}
