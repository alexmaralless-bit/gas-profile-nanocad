using System;
using System.Collections.Generic;
using System.Globalization;
using GasProfile.Core.Geometry;
using GasProfile.Core.Profile;
using HostMgd.ApplicationServices;
using HostMgd.EditorInput;
using Teigha.DatabaseServices;
using Teigha.Runtime;

namespace GasProfile.NanoCad.Commands
{
    public sealed class CheckCommand
    {
        [CommandMethod("GASPROF_CHECK")]
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
                if (routePolyline == null)
                {
                    editor.WriteMessage("\nВыбранный объект трассы не является 2D Polyline.");
                    return;
                }

                var samplePoint = transaction.GetObject(pointResult.ObjectId, OpenMode.ForRead) as DBPoint;
                if (samplePoint == null)
                {
                    editor.WriteMessage("\nВыбранный объект отметки пока не поддержан. Для MVP выберите DBPoint.");
                    return;
                }

                var route = ReadRoute(routePolyline);
                var surfacePoints = CollectDbPointsOnLayer(database, transaction, samplePoint.Layer);

                WriteReport(editor, routePolyline.Layer, route, samplePoint.Layer, surfacePoints);
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
            var modelSpaceId = blockTable[BlockTableRecord.ModelSpace];
            var modelSpace = (BlockTableRecord)transaction.GetObject(modelSpaceId, OpenMode.ForRead);

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
            string routeLayer,
            Route2D route,
            string pointLayer,
            IReadOnlyList<Point3> surfacePoints)
        {
            editor.WriteMessage("\n--- GASPROF_CHECK ---");
            editor.WriteMessage($"\nСлой трассы: {routeLayer}");
            editor.WriteMessage($"\nВершин трассы: {route.Vertices.Count}");
            editor.WriteMessage($"\nДлина трассы: {route.Length.ToString("0.00", CultureInfo.InvariantCulture)} м");
            editor.WriteMessage($"\nКонечный пикет: {ChainageFormatter.Format(route.Length)}");
            editor.WriteMessage($"\nСлой точек поверхности: {pointLayer}");
            editor.WriteMessage($"\nНайдено DBPoint на слое: {surfacePoints.Count}");

            if (surfacePoints.Count > 0)
            {
                var minZ = double.PositiveInfinity;
                var maxZ = double.NegativeInfinity;
                for (var i = 0; i < surfacePoints.Count; i++)
                {
                    minZ = Math.Min(minZ, surfacePoints[i].Z);
                    maxZ = Math.Max(maxZ, surfacePoints[i].Z);
                }

                editor.WriteMessage($"\nМинимальная отметка: {minZ.ToString("0.00", CultureInfo.InvariantCulture)}");
                editor.WriteMessage($"\nМаксимальная отметка: {maxZ.ToString("0.00", CultureInfo.InvariantCulture)}");
            }

            if (surfacePoints.Count < 3)
            {
                editor.WriteMessage("\nВНИМАНИЕ: для TIN нужно минимум 3 точки поверхности.");
            }

            editor.WriteMessage("\n---------------------");
        }
    }
}
