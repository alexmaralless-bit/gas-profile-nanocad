using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GasProfile.Core.Geometry;
using GasProfile.Core.Profile;
using HostMgd.ApplicationServices;
using HostMgd.EditorInput;
using Teigha.Colors;
using Teigha.DatabaseServices;
using Teigha.Geometry;
using Teigha.Runtime;

namespace GasProfile.NanoCad.Commands
{
    public sealed class AddCasingCommand
    {
        private const string BlockName = "GASPROF_CASING_PLAN";
        private const string LayerName = AddSituationCommand.LayerName;
        private const string AttrDn = CasingAttributeNames.Dn;
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
        private static readonly LineWeight CasingLineWeight = LineWeight.LineWeight025;

        [CommandMethod("GASPROF_ADD_CASING")]
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

            var pointResult = editor.GetPoint("\nУкажите центр футляра на плане: ");
            if (pointResult.Status != PromptStatus.OK)
            {
                return;
            }

            var length = GetDouble(editor, "\nДлина футляра, м", 12.00);
            var dn = GetString(editor, "\nПодпись футляра", "Футляр DN250x22.7");
            var material = GetString(editor, "\nМатериал футляра", CasingAttributeNames.DefaultMaterial);
            var hasControlTube = GetYesNo(editor, "\nКонтрольная трубка", true);
            var normOffsetStart = GetDouble(editor, "\nНормативный отступ от начала футляра, м", CasingAttributeNames.DefaultNormOffset);
            var normOffsetEnd = GetDouble(editor, "\nНормативный отступ от конца футляра, м", CasingAttributeNames.DefaultNormOffset);

            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var routePolyline = transaction.GetObject(routeResult.ObjectId, OpenMode.ForRead) as Polyline;
                if (routePolyline == null)
                {
                    editor.WriteMessage("\nДля вставки футляра нужна 2D Polyline трассы.");
                    return;
                }

                var route = ReadRoute(routePolyline);
                var pickedPoint = new Point2(pointResult.Value.X, pointResult.Value.Y);
                var centerChainage = route.GetClosestChainage(pickedPoint);
                var rotation = GetRouteRotation(route, centerChainage);
                var centerPoint = new Point3d(pointResult.Value.X, pointResult.Value.Y, 0.0);
                var start = Clamp(centerChainage - length * 0.5, 0.0, route.Length);
                var end = Clamp(centerChainage + length * 0.5, 0.0, route.Length);

                EnsureLayer(database, transaction);
                var blockId = EnsureBlock(database, transaction);
                var blockReference = InsertCasingBlock(
                    database,
                    transaction,
                    blockId,
                    centerPoint,
                    rotation,
                    length,
                    dn,
                    material,
                    hasControlTube,
                    normOffsetStart,
                    normOffsetEnd,
                    start,
                    end,
                    centerChainage);

                var block = (BlockTableRecord)transaction.GetObject(blockReference.BlockTableRecord, OpenMode.ForRead);
                UpdateAttribute(transaction, blockReference, AttrGeomSignature, ComputeCasingStateSignature(BlockName, blockReference, block, transaction));

                transaction.Commit();

                editor.WriteMessage("\n--- GASPROF_ADD_CASING ---");
                editor.WriteMessage($"\nФутляр: {dn}");
                editor.WriteMessage($"\nМатериал: {material}");
                editor.WriteMessage($"\nКонтрольная трубка: {(hasControlTube ? "Да" : "Нет")}");
                editor.WriteMessage($"\nНормативные отступы: {FormatNumber(normOffsetStart)} / {FormatNumber(normOffsetEnd)} м");
                editor.WriteMessage($"\nПикетаж: {ChainageFormatter.Format(start)} - {ChainageFormatter.Format(end)}");
                editor.WriteMessage($"\nДлина: {FormatNumber(end - start)} м");
                editor.WriteMessage("\nПосле ручного перемещения/растягивания запустите GASPROF_RECALC_CASINGS или GREC.");
                editor.WriteMessage("\n--------------------------");
            }
        }

        private static ObjectId EnsureBlock(Database database, Transaction transaction)
        {
            var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            if (blockTable.Has(BlockName))
            {
                var existingBlock = (BlockTableRecord)transaction.GetObject(blockTable[BlockName], OpenMode.ForWrite);
                EnsureAttributeDefinition(existingBlock, transaction, AttrDn, new Point3d(-0.35, 0.95, 0.0), false);
                EnsureHiddenAttributeDefinitions(existingBlock, transaction);
                return blockTable[BlockName];
            }

            blockTable.UpgradeOpen();
            var block = new BlockTableRecord
            {
                Name = BlockName
            };

            var blockId = blockTable.Add(block);
            transaction.AddNewlyCreatedDBObject(block, true);

            AddPolyline(block, transaction, new[]
            {
                new Point2d(-0.5, 0.28),
                new Point2d(0.5, 0.28),
                new Point2d(0.5, -0.28),
                new Point2d(-0.5, -0.28),
                new Point2d(-0.5, 0.28)
            });

            AddPolyline(block, transaction, new[]
            {
                new Point2d(-0.5, 0.12),
                new Point2d(0.5, 0.12),
                new Point2d(0.5, -0.12),
                new Point2d(-0.5, -0.12),
                new Point2d(-0.5, 0.12)
            });

            var controlTube = new Circle(new Point3d(0.35, 0.45, 0.0), Vector3d.ZAxis, 0.12)
            {
                Layer = "0",
                Linetype = "Continuous",
                LineWeight = CasingLineWeight
            };
            block.AppendEntity(controlTube);
            transaction.AddNewlyCreatedDBObject(controlTube, true);

            AddAttributeDefinition(block, transaction, AttrDn, new Point3d(-0.35, 0.95, 0.0), false);
            EnsureHiddenAttributeDefinitions(block, transaction);
            return blockId;
        }

        private static void AddPolyline(BlockTableRecord block, Transaction transaction, IReadOnlyList<Point2d> vertices)
        {
            var polyline = new Polyline
            {
                Layer = "0",
                Linetype = "Continuous",
                LineWeight = CasingLineWeight
            };

            for (var i = 0; i < vertices.Count; i++)
            {
                polyline.AddVertexAt(i, vertices[i], 0.0, 0.0, 0.0);
            }

            block.AppendEntity(polyline);
            transaction.AddNewlyCreatedDBObject(polyline, true);
        }

        private static void EnsureHiddenAttributeDefinitions(BlockTableRecord block, Transaction transaction)
        {
            EnsureAttributeDefinition(block, transaction, AttrStartChainage, new Point3d(0.0, -1.0, 0.0), true);
            EnsureAttributeDefinition(block, transaction, AttrEndChainage, new Point3d(0.0, -1.2, 0.0), true);
            EnsureAttributeDefinition(block, transaction, AttrStartPicket, new Point3d(0.0, -1.4, 0.0), true);
            EnsureAttributeDefinition(block, transaction, AttrEndPicket, new Point3d(0.0, -1.6, 0.0), true);
            EnsureAttributeDefinition(block, transaction, AttrCenterChainage, new Point3d(0.0, -1.8, 0.0), true);
            EnsureAttributeDefinition(block, transaction, AttrCenterPicket, new Point3d(0.0, -2.0, 0.0), true);
            EnsureAttributeDefinition(block, transaction, AttrLength, new Point3d(0.0, -2.2, 0.0), true);
            EnsureAttributeDefinition(block, transaction, AttrMaterial, new Point3d(0.0, -2.4, 0.0), true);
            EnsureAttributeDefinition(block, transaction, AttrHasControlTube, new Point3d(0.0, -2.6, 0.0), true);
            EnsureAttributeDefinition(block, transaction, AttrControlTubeChainage, new Point3d(0.0, -2.8, 0.0), true);
            EnsureAttributeDefinition(block, transaction, AttrControlTubePicket, new Point3d(0.0, -3.0, 0.0), true);
            EnsureAttributeDefinition(block, transaction, AttrNormOffsetStart, new Point3d(0.0, -3.2, 0.0), true);
            EnsureAttributeDefinition(block, transaction, AttrNormOffsetEnd, new Point3d(0.0, -3.4, 0.0), true);
            EnsureAttributeDefinition(block, transaction, AttrGeomSignature, new Point3d(0.0, -3.6, 0.0), true);
        }

        private static void EnsureAttributeDefinition(
            BlockTableRecord block,
            Transaction transaction,
            string tag,
            Point3d position,
            bool invisible)
        {
            foreach (ObjectId objectId in block)
            {
                var existing = transaction.GetObject(objectId, OpenMode.ForRead, false) as AttributeDefinition;
                if (existing != null && string.Equals(existing.Tag, tag, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            AddAttributeDefinition(block, transaction, tag, position, invisible);
        }

        private static void AddAttributeDefinition(
            BlockTableRecord block,
            Transaction transaction,
            string tag,
            Point3d position,
            bool invisible)
        {
            var attribute = new AttributeDefinition
            {
                Tag = tag,
                Prompt = tag,
                TextString = "",
                Position = position,
                Height = invisible ? 1.0 : 0.65,
                Verifiable = false,
                Invisible = invisible
            };

            block.AppendEntity(attribute);
            transaction.AddNewlyCreatedDBObject(attribute, true);
        }

        private static BlockReference InsertCasingBlock(
            Database database,
            Transaction transaction,
            ObjectId blockTableRecordId,
            Point3d insertPoint,
            double rotation,
            double length,
            string dn,
            string material,
            bool hasControlTube,
            double normOffsetStart,
            double normOffsetEnd,
            double startChainage,
            double endChainage,
            double centerChainage)
        {
            var modelSpace = GetModelSpace(database, transaction, OpenMode.ForWrite);
            var blockReference = new BlockReference(insertPoint, blockTableRecordId)
            {
                Layer = LayerName,
                Linetype = "Continuous",
                LineWeight = CasingLineWeight,
                Rotation = rotation,
                ScaleFactors = new Scale3d(Math.Max(length, 0.001), 1.0, 1.0)
            };

            modelSpace.AppendEntity(blockReference);
            transaction.AddNewlyCreatedDBObject(blockReference, true);

            var block = (BlockTableRecord)transaction.GetObject(blockTableRecordId, OpenMode.ForRead);
            foreach (ObjectId objectId in block)
            {
                var definition = transaction.GetObject(objectId, OpenMode.ForRead) as AttributeDefinition;
                if (definition == null || definition.Constant)
                {
                    continue;
                }

                var reference = new AttributeReference();
                reference.SetAttributeFromBlock(definition, blockReference.BlockTransform);
                reference.TextString = GetAttributeValue(
                    definition.Tag,
                    dn,
                    material,
                    hasControlTube,
                    normOffsetStart,
                    normOffsetEnd,
                    startChainage,
                    endChainage,
                    centerChainage);
                if (string.Equals(definition.Tag, AttrDn, StringComparison.OrdinalIgnoreCase))
                {
                    reference.WidthFactor = Math.Max(0.05, 1.0 / Math.Max(length, 1.0));
                }

                blockReference.AttributeCollection.AppendAttribute(reference);
                transaction.AddNewlyCreatedDBObject(reference, true);
            }

            return blockReference;
        }

        private static string GetAttributeValue(
            string tag,
            string dn,
            string material,
            bool hasControlTube,
            double normOffsetStart,
            double normOffsetEnd,
            double startChainage,
            double endChainage,
            double centerChainage)
        {
            var length = Math.Abs(endChainage - startChainage);
            var controlTubeChainage = startChainage + length * 0.78;
            switch (tag.ToUpperInvariant())
            {
                case AttrDn:
                    return dn;
                case AttrMaterial:
                    return material;
                case AttrHasControlTube:
                    return hasControlTube ? CasingAttributeNames.DefaultHasControlTube : "NO";
                case AttrNormOffsetStart:
                    return FormatNumber(normOffsetStart);
                case AttrNormOffsetEnd:
                    return FormatNumber(normOffsetEnd);
                case AttrControlTubeChainage:
                    return FormatNumber(controlTubeChainage);
                case AttrControlTubePicket:
                    return ChainageFormatter.Format(controlTubeChainage);
                case AttrStartChainage:
                    return FormatNumber(startChainage);
                case AttrEndChainage:
                    return FormatNumber(endChainage);
                case AttrStartPicket:
                    return ChainageFormatter.Format(startChainage);
                case AttrEndPicket:
                    return ChainageFormatter.Format(endChainage);
                case AttrCenterChainage:
                    return FormatNumber(centerChainage);
                case AttrCenterPicket:
                    return ChainageFormatter.Format(centerChainage);
                case AttrLength:
                    return FormatNumber(length);
                default:
                    return "";
            }
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

        private static double GetRouteRotation(Route2D route, double chainage)
        {
            var target = Clamp(chainage, 0.0, route.Length);
            for (var i = 0; i < route.SegmentCount; i++)
            {
                var start = route.GetVertexChainage(i);
                var end = route.GetVertexChainage(i + 1);
                if (target > end && i < route.SegmentCount - 1)
                {
                    continue;
                }

                var p1 = route.Vertices[i];
                var p2 = route.Vertices[i + 1];
                return Math.Atan2(p2.Y - p1.Y, p2.X - p1.X);
            }

            var lastStart = route.Vertices[route.Vertices.Count - 2];
            var lastEnd = route.Vertices[route.Vertices.Count - 1];
            return Math.Atan2(lastEnd.Y - lastStart.Y, lastEnd.X - lastStart.X);
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
                        startPoint = points[i];
                        endPoint = points[j];
                    }
                }
            }

            return bestLengthSquared > 0.000001;
        }

        private static double GetDouble(Editor editor, string message, double defaultValue)
        {
            var options = new PromptDoubleOptions($"{message} <{defaultValue.ToString("0.00", CultureInfo.InvariantCulture)}>: ")
            {
                AllowNone = true,
                AllowNegative = false,
                AllowZero = false,
                DefaultValue = defaultValue
            };

            var result = editor.GetDouble(options);
            return result.Status == PromptStatus.OK ? result.Value : defaultValue;
        }

        private static string GetString(Editor editor, string message, string defaultValue)
        {
            var suffix = string.IsNullOrWhiteSpace(defaultValue) ? "" : $" <{defaultValue}>";
            var options = new PromptStringOptions($"{message}{suffix}: ")
            {
                AllowSpaces = true
            };

            var result = editor.GetString(options);
            if (result.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(result.StringResult))
            {
                return defaultValue;
            }

            return result.StringResult.Trim();
        }

        private static bool GetYesNo(Editor editor, string message, bool defaultValue)
        {
            var defaultText = defaultValue ? "Да" : "Нет";
            var value = GetString(editor, message, defaultText);
            return value.Equals("Да", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("Д", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("Yes", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("Y", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("True", StringComparison.OrdinalIgnoreCase);
        }

        private static BlockTableRecord GetModelSpace(Database database, Transaction transaction, OpenMode mode)
        {
            var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            return (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], mode);
        }

        private static void EnsureLayer(Database database, Transaction transaction)
        {
            var layerTable = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            if (layerTable.Has(LayerName))
            {
                var existing = (LayerTableRecord)transaction.GetObject(layerTable[LayerName], OpenMode.ForWrite);
                existing.Color = Color.FromColorIndex(ColorMethod.ByAci, 2);
                existing.LineWeight = CasingLineWeight;
                return;
            }

            layerTable.UpgradeOpen();
            var layer = new LayerTableRecord
            {
                Name = LayerName,
                Color = Color.FromColorIndex(ColorMethod.ByAci, 2),
                LineWeight = CasingLineWeight
            };

            layerTable.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
        }

        private static double Clamp(double value, double min, double max)
        {
            return Math.Max(min, Math.Min(max, value));
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
