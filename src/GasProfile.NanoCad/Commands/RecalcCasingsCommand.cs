using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GasProfile.Core.Geometry;
using GasProfile.Core.Profile;
using HostMgd.ApplicationServices;
using HostMgd.EditorInput;
using Teigha.DatabaseServices;
using Teigha.Geometry;
using Teigha.Runtime;

namespace GasProfile.NanoCad.Commands
{
    public sealed class RecalcCasingsCommand
    {
        private const string AttrMaterial = CasingAttributeNames.Material;
        private const string AttrHasControlTube = CasingAttributeNames.HasControlTube;
        private const string AttrNormOffsetStart = CasingAttributeNames.NormOffsetStart;
        private const string AttrNormOffsetEnd = CasingAttributeNames.NormOffsetEnd;
        private const string AttrControlTubeChainage = CasingAttributeNames.ControlTubeChainage;
        private const string AttrControlTubePicket = CasingAttributeNames.ControlTubePicket;
        private const string AttrStartChainage = CasingAttributeNames.StartChainage;
        private const string AttrEndChainage = CasingAttributeNames.EndChainage;
        private const string AttrStartPicket = CasingAttributeNames.StartPicket;
        private const string AttrEndPicket = CasingAttributeNames.EndPicket;
        private const string AttrCenterChainage = CasingAttributeNames.CenterChainage;
        private const string AttrCenterPicket = CasingAttributeNames.CenterPicket;
        private const string AttrLength = CasingAttributeNames.Length;
        private const string AttrGeomSignature = CasingAttributeNames.GeomSignature;

        [CommandMethod("GASPROF_RECALC_CASINGS")]
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

            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var routePolyline = transaction.GetObject(routeResult.ObjectId, OpenMode.ForRead) as Polyline;
                if (routePolyline == null)
                {
                    editor.WriteMessage("\nДля пересчета нужна 2D Polyline.");
                    return;
                }

                var route = ReadRoute(routePolyline);
                var modelSpace = GetModelSpace(database, transaction, OpenMode.ForRead);
                var count = 0;

                editor.WriteMessage("\n--- GASPROF_RECALC_CASINGS ---");
                foreach (ObjectId objectId in modelSpace)
                {
                    var blockReference = transaction.GetObject(objectId, OpenMode.ForWrite, false) as BlockReference;
                    if (blockReference == null)
                    {
                        continue;
                    }

                    var block = transaction.GetObject(blockReference.BlockTableRecord, OpenMode.ForRead, false) as BlockTableRecord;
                    if (block == null)
                    {
                        continue;
                    }

                    var attrs = ReadAttributes(transaction, blockReference);
                    var blockName = GetEffectiveBlockName(transaction, blockReference, block);
                    if (!IsCasing(blockName, attrs))
                    {
                        continue;
                    }

                    if (!TryGetProjectedBlockGeometryInterval(route, blockReference, block, transaction, out var start, out var end) &&
                        !TryGetProjectedExtentsInterval(route, blockReference, out start, out end))
                    {
                        continue;
                    }

                    start = Math.Max(0.0, Math.Min(route.Length, start));
                    end = Math.Max(0.0, Math.Min(route.Length, end));
                    if (end < start)
                    {
                        var swap = start;
                        start = end;
                        end = swap;
                    }

                    var casing = CasingData.FromAttributes(blockName, attrs, start, end);

                    UpdateAttribute(transaction, blockReference, AttrStartChainage, FormatNumber(casing.StartChainage));
                    UpdateAttribute(transaction, blockReference, AttrEndChainage, FormatNumber(casing.EndChainage));
                    UpdateAttribute(transaction, blockReference, AttrStartPicket, casing.StartPicket);
                    UpdateAttribute(transaction, blockReference, AttrEndPicket, casing.EndPicket);
                    UpdateAttribute(transaction, blockReference, AttrCenterChainage, FormatNumber(casing.CenterChainage));
                    UpdateAttribute(transaction, blockReference, AttrCenterPicket, ChainageFormatter.Format(casing.CenterChainage));
                    UpdateAttribute(transaction, blockReference, AttrLength, FormatNumber(casing.Length));
                    UpdateAttribute(transaction, blockReference, AttrMaterial, casing.Material);
                    UpdateAttribute(transaction, blockReference, AttrHasControlTube, casing.HasControlTube ? CasingAttributeNames.DefaultHasControlTube : "NO");
                    UpdateAttribute(transaction, blockReference, AttrControlTubeChainage, FormatNumber(casing.ControlTubeChainage));
                    UpdateAttribute(transaction, blockReference, AttrControlTubePicket, casing.ControlTubePicket);
                    UpdateAttribute(transaction, blockReference, AttrNormOffsetStart, FormatNumber(casing.NormOffsetStart));
                    UpdateAttribute(transaction, blockReference, AttrNormOffsetEnd, FormatNumber(casing.NormOffsetEnd));
                    UpdateAttribute(transaction, blockReference, AttrGeomSignature, ComputeCasingStateSignature(blockName, blockReference, block, transaction));

                    count++;
                    editor.WriteMessage($"\n{blockName}: {casing.PicketRange}, L={FormatNumber(casing.Length)} м, материал={casing.Material}, КТ={(casing.HasControlTube ? "YES" : "NO")}");
                }

                transaction.Commit();
                editor.WriteMessage($"\nПересчитано футляров: {count}");
                editor.WriteMessage("\n------------------------------");
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

        private static BlockTableRecord GetModelSpace(Database database, Transaction transaction, OpenMode mode)
        {
            var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            return (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], mode);
        }

        private static bool IsCasing(string blockName, IReadOnlyDictionary<string, string> attrs)
        {
            if (!string.IsNullOrWhiteSpace(blockName) &&
                blockName.IndexOf("Футляр", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return attrs.TryGetValue("DN", out var dn) &&
                !string.IsNullOrWhiteSpace(dn) &&
                dn.IndexOf("Футляр", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static Dictionary<string, string> ReadAttributes(Transaction transaction, BlockReference blockReference)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId attributeId in blockReference.AttributeCollection)
            {
                var attribute = transaction.GetObject(attributeId, OpenMode.ForRead, false) as AttributeReference;
                if (attribute != null)
                {
                    result[attribute.Tag] = attribute.TextString;
                }
            }

            return result;
        }

        private static void UpdateAttribute(
            Transaction transaction,
            BlockReference blockReference,
            string tag,
            string value)
        {
            foreach (ObjectId attributeId in blockReference.AttributeCollection)
            {
                var attribute = transaction.GetObject(attributeId, OpenMode.ForWrite, false) as AttributeReference;
                if (attribute == null || !string.Equals(attribute.Tag, tag, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                attribute.TextString = value;
                return;
            }

            var newAttribute = new AttributeReference
            {
                Tag = tag,
                TextString = value,
                Position = blockReference.Position,
                Height = 1.0,
                Invisible = true
            };

            blockReference.AttributeCollection.AppendAttribute(newAttribute);
            transaction.AddNewlyCreatedDBObject(newAttribute, true);
        }

        private static bool TryGetProjectedBlockGeometryInterval(
            Route2D route,
            BlockReference blockReference,
            BlockTableRecord block,
            Transaction transaction,
            out double start,
            out double end)
        {
            start = 0.0;
            end = 0.0;
            var points = new List<Point2>();

            foreach (ObjectId entityId in block)
            {
                var entity = transaction.GetObject(entityId, OpenMode.ForRead, false) as Entity;
                if (entity == null || string.Equals(entity.Layer, "Defpoints", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (entity is Polyline polyline)
                {
                    for (var i = 0; i < polyline.NumberOfVertices; i++)
                    {
                        var point = polyline.GetPoint3dAt(i).TransformBy(blockReference.BlockTransform);
                        points.Add(new Point2(point.X, point.Y));
                    }

                    continue;
                }

                if (entity is Line line)
                {
                    var startPoint = line.StartPoint.TransformBy(blockReference.BlockTransform);
                    var endPoint = line.EndPoint.TransformBy(blockReference.BlockTransform);
                    points.Add(new Point2(startPoint.X, startPoint.Y));
                    points.Add(new Point2(endPoint.X, endPoint.Y));
                }
            }

            return TryGetProjectedAxisInterval(route, points, out start, out end);
        }

        private static string ComputeCasingStateSignature(
            string blockName,
            BlockReference blockReference,
            BlockTableRecord block,
            Transaction transaction)
        {
            var points = CollectCasingGeometryPoints(blockReference, block, transaction);
            TryGetAxisEnds(points, out var startPoint, out var endPoint);

            var dx = endPoint.X - startPoint.X;
            var dy = endPoint.Y - startPoint.Y;
            var axisLength = Math.Sqrt(dx * dx + dy * dy);
            var axisAngle = Math.Atan2(dy, dx);
            var raw = string.Join("|", new[]
            {
                blockName,
                FormatHashNumber(blockReference.Position.X),
                FormatHashNumber(blockReference.Position.Y),
                FormatHashNumber(blockReference.Rotation),
                FormatHashNumber(blockReference.ScaleFactors.X),
                FormatHashNumber(blockReference.ScaleFactors.Y),
                FormatHashNumber(startPoint.X),
                FormatHashNumber(startPoint.Y),
                FormatHashNumber(endPoint.X),
                FormatHashNumber(endPoint.Y),
                FormatHashNumber(axisLength),
                FormatHashNumber(axisAngle)
            });

            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                return BitConverter.ToString(bytes).Replace("-", "").Substring(0, 24);
            }
        }

        private static List<Point2> CollectCasingGeometryPoints(
            BlockReference blockReference,
            BlockTableRecord block,
            Transaction transaction)
        {
            var points = new List<Point2>();
            foreach (ObjectId entityId in block)
            {
                var entity = transaction.GetObject(entityId, OpenMode.ForRead, false) as Entity;
                if (entity == null || string.Equals(entity.Layer, "Defpoints", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (entity is Polyline polyline)
                {
                    for (var i = 0; i < polyline.NumberOfVertices; i++)
                    {
                        var point = polyline.GetPoint3dAt(i).TransformBy(blockReference.BlockTransform);
                        points.Add(new Point2(point.X, point.Y));
                    }

                    continue;
                }

                if (entity is Line line)
                {
                    var startPoint = line.StartPoint.TransformBy(blockReference.BlockTransform);
                    var endPoint = line.EndPoint.TransformBy(blockReference.BlockTransform);
                    points.Add(new Point2(startPoint.X, startPoint.Y));
                    points.Add(new Point2(endPoint.X, endPoint.Y));
                }
            }

            return points;
        }

        private static bool TryGetAxisEnds(IReadOnlyList<Point2> points, out Point2 startPoint, out Point2 endPoint)
        {
            startPoint = points.Count > 0 ? points[0] : new Point2(0.0, 0.0);
            endPoint = points.Count > 1 ? points[1] : startPoint;
            if (points.Count < 2)
            {
                return false;
            }

            var axisStart = points[0];
            var axisEnd = points[1];
            var bestLengthSquared = 0.0;
            for (var i = 0; i < points.Count; i++)
            {
                for (var j = i + 1; j < points.Count; j++)
                {
                    var dx = points[j].X - points[i].X;
                    var dy = points[j].Y - points[i].Y;
                    var lengthSquared = dx * dx + dy * dy;
                    if (lengthSquared > bestLengthSquared)
                    {
                        bestLengthSquared = lengthSquared;
                        axisStart = points[i];
                        axisEnd = points[j];
                    }
                }
            }

            if (bestLengthSquared < 0.000001)
            {
                return false;
            }

            var axisLength = Math.Sqrt(bestLengthSquared);
            var ux = (axisEnd.X - axisStart.X) / axisLength;
            var uy = (axisEnd.Y - axisStart.Y) / axisLength;
            var minProjection = double.MaxValue;
            var maxProjection = double.MinValue;
            var projections = new double[points.Count];

            for (var i = 0; i < points.Count; i++)
            {
                var projection = (points[i].X - axisStart.X) * ux + (points[i].Y - axisStart.Y) * uy;
                projections[i] = projection;
                minProjection = Math.Min(minProjection, projection);
                maxProjection = Math.Max(maxProjection, projection);
            }

            var tolerance = Math.Max(0.05, (maxProjection - minProjection) * 0.05);
            startPoint = AverageProjectedEnd(points, projections, minProjection, tolerance);
            endPoint = AverageProjectedEnd(points, projections, maxProjection, tolerance);
            return true;
        }

        private static bool TryGetProjectedAxisInterval(
            Route2D route,
            IReadOnlyList<Point2> points,
            out double start,
            out double end)
        {
            start = 0.0;
            end = 0.0;
            if (points.Count < 2)
            {
                return false;
            }

            var axisStart = points[0];
            var axisEnd = points[1];
            var bestLengthSquared = 0.0;
            for (var i = 0; i < points.Count; i++)
            {
                for (var j = i + 1; j < points.Count; j++)
                {
                    var dx = points[j].X - points[i].X;
                    var dy = points[j].Y - points[i].Y;
                    var lengthSquared = dx * dx + dy * dy;
                    if (lengthSquared > bestLengthSquared)
                    {
                        bestLengthSquared = lengthSquared;
                        axisStart = points[i];
                        axisEnd = points[j];
                    }
                }
            }

            if (bestLengthSquared < 0.000001)
            {
                return false;
            }

            var axisLength = Math.Sqrt(bestLengthSquared);
            var ux = (axisEnd.X - axisStart.X) / axisLength;
            var uy = (axisEnd.Y - axisStart.Y) / axisLength;
            var minProjection = double.MaxValue;
            var maxProjection = double.MinValue;
            var projections = new double[points.Count];

            for (var i = 0; i < points.Count; i++)
            {
                var projection = (points[i].X - axisStart.X) * ux + (points[i].Y - axisStart.Y) * uy;
                projections[i] = projection;
                minProjection = Math.Min(minProjection, projection);
                maxProjection = Math.Max(maxProjection, projection);
            }

            var tolerance = Math.Max(0.05, (maxProjection - minProjection) * 0.05);
            var startPoint = AverageProjectedEnd(points, projections, minProjection, tolerance);
            var endPoint = AverageProjectedEnd(points, projections, maxProjection, tolerance);
            start = route.GetClosestChainage(startPoint);
            end = route.GetClosestChainage(endPoint);
            return Math.Abs(end - start) > 0.001;
        }

        private static Point2 AverageProjectedEnd(
            IReadOnlyList<Point2> points,
            IReadOnlyList<double> projections,
            double target,
            double tolerance)
        {
            var x = 0.0;
            var y = 0.0;
            var count = 0;
            for (var i = 0; i < points.Count; i++)
            {
                if (Math.Abs(projections[i] - target) > tolerance)
                {
                    continue;
                }

                x += points[i].X;
                y += points[i].Y;
                count++;
            }

            return count == 0
                ? points[0]
                : new Point2(x / count, y / count);
        }

        private static void AddProjectedChainage(Route2D route, Point2 point, ref double start, ref double end)
        {
            var chainage = route.GetClosestChainage(point);
            if (chainage < start)
            {
                start = chainage;
            }

            if (chainage > end)
            {
                end = chainage;
            }
        }

        private static bool TryGetProjectedExtentsInterval(
            Route2D route,
            BlockReference blockReference,
            out double start,
            out double end)
        {
            start = 0.0;
            end = 0.0;

            Extents3d extents;
            try
            {
                extents = blockReference.GeometricExtents;
            }
            catch
            {
                return false;
            }

            var min = extents.MinPoint;
            var max = extents.MaxPoint;
            var corners = new[]
            {
                new Point2(min.X, min.Y),
                new Point2(min.X, max.Y),
                new Point2(max.X, min.Y),
                new Point2(max.X, max.Y)
            };

            start = double.MaxValue;
            end = double.MinValue;
            foreach (var corner in corners)
            {
                AddProjectedChainage(route, corner, ref start, ref end);
            }

            return start < double.MaxValue && end > double.MinValue;
        }

        private static string GetEffectiveBlockName(
            Transaction transaction,
            BlockReference blockReference,
            BlockTableRecord block)
        {
            try
            {
                if (!blockReference.DynamicBlockTableRecord.IsNull)
                {
                    var dynamicBlock = transaction.GetObject(blockReference.DynamicBlockTableRecord, OpenMode.ForRead, false) as BlockTableRecord;
                    if (dynamicBlock != null && !string.IsNullOrWhiteSpace(dynamicBlock.Name))
                    {
                        return dynamicBlock.Name;
                    }
                }
            }
            catch
            {
            }

            return block.Name;
        }

        private static string FormatNumber(double value)
        {
            return value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        private static string FormatHashNumber(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
