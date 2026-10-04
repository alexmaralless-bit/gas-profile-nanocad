using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GasProfile.Core.Geometry;
using GasProfile.Core.Profile;
using GasProfile.Core.Tin;
using HostMgd.ApplicationServices;
using HostMgd.EditorInput;
using Teigha.DatabaseServices;
using Teigha.Runtime;

namespace GasProfile.NanoCad.Commands
{
    public sealed class CheckTinCommand
    {
        [CommandMethod("GASPROF_CHECK_TIN")]
        public void Run()
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                return;
            }

            var editor = document.Editor;
            var database = document.Database;

            var routeResult = editor.GetEntity("\nВыберите 2D-полилинию трассы газопровода: ");
            if (routeResult.Status != PromptStatus.OK)
            {
                return;
            }

            var pointResult = editor.GetEntity("\nВыберите одну точку отметки поверхности: ");
            if (pointResult.Status != PromptStatus.OK)
            {
                return;
            }

            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var routePolyline = transaction.GetObject(routeResult.ObjectId, OpenMode.ForRead) as Polyline;
                var samplePoint = transaction.GetObject(pointResult.ObjectId, OpenMode.ForRead) as DBPoint;

                if (routePolyline == null || samplePoint == null)
                {
                    editor.WriteMessage("\nДля проверки TIN нужны 2D Polyline и DBPoint.");
                    return;
                }

                var route = ReadRoute(routePolyline);
                var surfacePoints = CollectDbPointsOnLayer(database, transaction, samplePoint.Layer);
                var tin = DelaunayTriangulator.Build(surfacePoints);
                var profilePoints = ProfileBuilder.BuildGroundProfile(route, tin);

                WriteReport(editor, route, surfacePoints.Count, tin, profilePoints);
                transaction.Commit();
            }
        }

        private static Route2D ReadRoute(Polyline polyline)
        {
            var vertices = new List<Point2>(polyline.NumberOfVertices);
            for (var i = 0; i < polyline.NumberOfVertices; i++)
            {
                var point = polyline.GetPoint2dAt(i);
                vertices.Add(new Point2(point.X, point.Y));
            }

            return new Route2D(vertices);
        }

        private static List<Point3> CollectDbPointsOnLayer(Database database, Transaction transaction, string layer)
        {
            var result = new List<Point3>();
            var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            var modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            foreach (ObjectId objectId in modelSpace)
            {
                var dbPoint = transaction.GetObject(objectId, OpenMode.ForRead, false) as DBPoint;
                if (dbPoint == null || !string.Equals(dbPoint.Layer, layer, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var position = dbPoint.Position;
                result.Add(new Point3(position.X, position.Y, position.Z));
            }

            return result;
        }

        private static void WriteReport(
            Editor editor,
            Route2D route,
            int sourcePointCount,
            TinSurface tin,
            IReadOnlyList<ProfilePoint> profilePoints)
        {
            var outsideCount = profilePoints.Count(p => !p.GroundElevation.HasValue);
            var knownElevations = profilePoints
                .Where(p => p.GroundElevation.HasValue)
                .Select(p => p.GroundElevation!.Value)
                .ToList();

            editor.WriteMessage("\n--- GASPROF_CHECK_TIN ---");
            editor.WriteMessage($"\nТочек поверхности исходных: {sourcePointCount}");
            editor.WriteMessage($"\nТочек поверхности уникальных XY: {tin.Points.Count}");
            editor.WriteMessage($"\nТреугольников TIN: {tin.Triangles.Count}");
            editor.WriteMessage($"\nРебер TIN: {tin.Edges.Count}");
            editor.WriteMessage($"\nДлина трассы: {route.Length.ToString("0.00", CultureInfo.InvariantCulture)} м");
            editor.WriteMessage($"\nРасчетных точек профиля: {profilePoints.Count}");
            editor.WriteMessage($"\nТочек вне TIN: {outsideCount}");

            if (knownElevations.Count > 0)
            {
                editor.WriteMessage($"\nMin Z земли по трассе: {knownElevations.Min().ToString("0.00", CultureInfo.InvariantCulture)}");
                editor.WriteMessage($"\nMax Z земли по трассе: {knownElevations.Max().ToString("0.00", CultureInfo.InvariantCulture)}");
            }

            foreach (var point in profilePoints.Take(8))
            {
                var z = point.GroundElevation.HasValue
                    ? point.GroundElevation.Value.ToString("0.00", CultureInfo.InvariantCulture)
                    : "OUT";
                editor.WriteMessage($"\n{ChainageFormatter.Format(point.Chainage)} {point.Type} Z={z}");
            }

            if (profilePoints.Count > 8)
            {
                editor.WriteMessage($"\n... еще {profilePoints.Count - 8} точек");
            }

            editor.WriteMessage("\n-------------------------");
        }
    }
}
