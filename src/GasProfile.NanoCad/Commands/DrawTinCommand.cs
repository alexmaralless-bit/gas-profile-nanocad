using System;
using System.Collections.Generic;
using System.Globalization;
using GasProfile.Core.Geometry;
using GasProfile.Core.Tin;
using HostMgd.ApplicationServices;
using HostMgd.EditorInput;
using Teigha.Colors;
using Teigha.DatabaseServices;
using Teigha.Geometry;
using Teigha.Runtime;

namespace GasProfile.NanoCad.Commands
{
    public sealed class DrawTinCommand
    {
        private const string TinLayer = "GASPROF_TIN";

        [CommandMethod("GASPROF_DRAW_TIN")]
        public void Run()
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                return;
            }

            var editor = document.Editor;
            var database = document.Database;

            var pointResult = editor.GetEntity("\nВыберите одну точку отметки поверхности: ");
            if (pointResult.Status != PromptStatus.OK)
            {
                return;
            }

            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var samplePoint = transaction.GetObject(pointResult.ObjectId, OpenMode.ForRead) as DBPoint;
                if (samplePoint == null)
                {
                    editor.WriteMessage("\nДля построения TIN нужна DBPoint.");
                    return;
                }

                var surfacePoints = CollectDbPointsOnLayer(database, transaction, samplePoint.Layer);
                var tin = DelaunayTriangulator.Build(surfacePoints);

                EnsureLayer(database, transaction);
                DrawTinEdges(database, transaction, tin);

                transaction.Commit();

                editor.WriteMessage("\n--- GASPROF_DRAW_TIN ---");
                editor.WriteMessage($"\nСлой точек поверхности: {samplePoint.Layer}");
                editor.WriteMessage($"\nТочек поверхности исходных: {surfacePoints.Count}");
                editor.WriteMessage($"\nТочек поверхности уникальных XY: {tin.Points.Count}");
                editor.WriteMessage($"\nТреугольников TIN: {tin.Triangles.Count}");
                editor.WriteMessage($"\nРебер TIN построено: {tin.Edges.Count.ToString(CultureInfo.InvariantCulture)}");
                editor.WriteMessage($"\nСлой TIN: {TinLayer}");
                editor.WriteMessage("\n------------------------");
            }
        }

        private static List<Point3> CollectDbPointsOnLayer(Database database, Transaction transaction, string layer)
        {
            var result = new List<Point3>();
            var modelSpace = GetModelSpace(database, transaction, OpenMode.ForRead);

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

        private static void DrawTinEdges(Database database, Transaction transaction, TinSurface tin)
        {
            var modelSpace = GetModelSpace(database, transaction, OpenMode.ForWrite);

            foreach (var edge in tin.Edges)
            {
                var a = tin.Points[edge.A];
                var b = tin.Points[edge.B];
                var line = new Line(new Point3d(a.X, a.Y, a.Z), new Point3d(b.X, b.Y, b.Z));
                line.Layer = TinLayer;
                modelSpace.AppendEntity(line);
                transaction.AddNewlyCreatedDBObject(line, true);
            }
        }

        private static BlockTableRecord GetModelSpace(Database database, Transaction transaction, OpenMode mode)
        {
            var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            return (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], mode);
        }

        private static void EnsureLayer(Database database, Transaction transaction)
        {
            var layerTable = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            if (layerTable.Has(TinLayer))
            {
                return;
            }

            layerTable.UpgradeOpen();
            var layer = new LayerTableRecord
            {
                Name = TinLayer,
                Color = Color.FromColorIndex(ColorMethod.ByAci, 8)
            };

            layerTable.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
        }
    }
}
