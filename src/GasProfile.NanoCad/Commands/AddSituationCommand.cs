using System;
using System.Collections.Generic;
using System.Globalization;
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
    public sealed class AddSituationCommand
    {
        internal const string BlockName = "GASPROF_SITUATION";
        internal const string LayerName = "GASPROF_SITUATION";
        internal const string AttrType = "TYPE";
        internal const string AttrName = "NAME";
        internal const string AttrChainage = "CHAINAGE";
        internal const string AttrPicket = "PICKET";
        internal const string AttrNote = "NOTE";
        internal const string AttrLength = "LENGTH";
        internal const string AttrDiameter = "DIAMETER";
        internal const string AttrMaterial = "MATERIAL";
        private static readonly LineWeight SituationLineWeight = LineWeight.LineWeight025;

        [CommandMethod("GASPROF_ADD_SITUATION")]
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

            var pointResult = editor.GetPoint("\nУкажите точку пересечения/ситуации на плане: ");
            if (pointResult.Status != PromptStatus.OK)
            {
                return;
            }

            var type = GetString(editor, "\nТип объекта", "Футляр");
            var name = GetString(editor, "\nНаименование", type);
            var isCasing = IsCasing(type);
            var length = isCasing ? GetDouble(editor, "\nДлина футляра, м", 6.00) : 0.0;
            var diameter = isCasing ? GetString(editor, "\nДиаметр футляра", "") : "";
            var material = isCasing ? GetString(editor, "\nМатериал футляра", "") : "";
            var note = GetString(editor, "\nПримечание", "");

            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var routePolyline = transaction.GetObject(routeResult.ObjectId, OpenMode.ForRead) as Polyline;
                if (routePolyline == null)
                {
                    editor.WriteMessage("\nДля привязки ситуации нужна 2D Polyline.");
                    return;
                }

                var route = ReadRoute(routePolyline);
                var insertPoint = pointResult.Value;
                var chainage = route.GetClosestChainage(new Point2(insertPoint.X, insertPoint.Y));
                var picket = ChainageFormatter.Format(chainage);

                EnsureLayer(database, transaction);
                var blockTableRecordId = EnsureBlock(database, transaction);
                InsertSituationBlock(
                    database,
                    transaction,
                    blockTableRecordId,
                    insertPoint,
                    type,
                    name,
                    chainage,
                    picket,
                    note,
                    length,
                    diameter,
                    material);

                transaction.Commit();

                editor.WriteMessage("\n--- GASPROF_ADD_SITUATION ---");
                editor.WriteMessage($"\nТип: {type}");
                editor.WriteMessage($"\nНаименование: {name}");
                editor.WriteMessage($"\nПикетаж: {picket}");
                editor.WriteMessage($"\nРасстояние: {chainage.ToString("0.00", CultureInfo.InvariantCulture)} м");
                if (isCasing)
                {
                    editor.WriteMessage($"\nДлина футляра: {length.ToString("0.00", CultureInfo.InvariantCulture)} м");
                }

                editor.WriteMessage("\n-----------------------------");
            }
        }

        private static bool IsCasing(string type)
        {
            return !string.IsNullOrWhiteSpace(type) &&
                type.Trim().ToLowerInvariant().Contains("фут");
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

        private static ObjectId EnsureBlock(Database database, Transaction transaction)
        {
            var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            if (blockTable.Has(BlockName))
            {
                var existingBlock = (BlockTableRecord)transaction.GetObject(blockTable[BlockName], OpenMode.ForWrite);
                EnsureAttributeDefinition(existingBlock, transaction, AttrType, new Point3d(1.2, 0.8, 0.0));
                EnsureAttributeDefinition(existingBlock, transaction, AttrName, new Point3d(1.2, -0.8, 0.0));
                EnsureAttributeDefinition(existingBlock, transaction, AttrChainage, new Point3d(1.2, -2.4, 0.0));
                EnsureAttributeDefinition(existingBlock, transaction, AttrPicket, new Point3d(1.2, -4.0, 0.0));
                EnsureAttributeDefinition(existingBlock, transaction, AttrNote, new Point3d(1.2, -5.6, 0.0));
                EnsureAttributeDefinition(existingBlock, transaction, AttrLength, new Point3d(1.2, -7.2, 0.0));
                EnsureAttributeDefinition(existingBlock, transaction, AttrDiameter, new Point3d(1.2, -8.8, 0.0));
                EnsureAttributeDefinition(existingBlock, transaction, AttrMaterial, new Point3d(1.2, -10.4, 0.0));
                return blockTable[BlockName];
            }

            blockTable.UpgradeOpen();
            var block = new BlockTableRecord
            {
                Name = BlockName
            };

            var blockId = blockTable.Add(block);
            transaction.AddNewlyCreatedDBObject(block, true);

            var circle = new Circle(Point3d.Origin, Vector3d.ZAxis, 0.75);
            circle.Layer = LayerName;
            circle.Linetype = "Continuous";
            circle.LineWeight = SituationLineWeight;
            block.AppendEntity(circle);
            transaction.AddNewlyCreatedDBObject(circle, true);

            AddAttributeDefinition(block, transaction, AttrType, new Point3d(1.2, 0.8, 0.0));
            AddAttributeDefinition(block, transaction, AttrName, new Point3d(1.2, -0.8, 0.0));
            AddAttributeDefinition(block, transaction, AttrChainage, new Point3d(1.2, -2.4, 0.0));
            AddAttributeDefinition(block, transaction, AttrPicket, new Point3d(1.2, -4.0, 0.0));
            AddAttributeDefinition(block, transaction, AttrNote, new Point3d(1.2, -5.6, 0.0));
            AddAttributeDefinition(block, transaction, AttrLength, new Point3d(1.2, -7.2, 0.0));
            AddAttributeDefinition(block, transaction, AttrDiameter, new Point3d(1.2, -8.8, 0.0));
            AddAttributeDefinition(block, transaction, AttrMaterial, new Point3d(1.2, -10.4, 0.0));

            return blockId;
        }

        private static void EnsureAttributeDefinition(
            BlockTableRecord block,
            Transaction transaction,
            string tag,
            Point3d position)
        {
            foreach (ObjectId objectId in block)
            {
                var existing = transaction.GetObject(objectId, OpenMode.ForRead, false) as AttributeDefinition;
                if (existing != null && string.Equals(existing.Tag, tag, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            AddAttributeDefinition(block, transaction, tag, position);
        }

        private static void AddAttributeDefinition(
            BlockTableRecord block,
            Transaction transaction,
            string tag,
            Point3d position)
        {
            var attribute = new AttributeDefinition
            {
                Tag = tag,
                Prompt = tag,
                TextString = "",
                Position = position,
                Height = 1.2,
                Verifiable = false,
                Invisible = true
            };

            block.AppendEntity(attribute);
            transaction.AddNewlyCreatedDBObject(attribute, true);
        }

        private static void InsertSituationBlock(
            Database database,
            Transaction transaction,
            ObjectId blockTableRecordId,
            Point3d insertPoint,
            string type,
            string name,
            double chainage,
            string picket,
            string note,
            double length,
            string diameter,
            string material)
        {
            var modelSpace = GetModelSpace(database, transaction, OpenMode.ForWrite);
            var blockReference = new BlockReference(insertPoint, blockTableRecordId)
            {
                Layer = LayerName,
                Linetype = "Continuous",
                LineWeight = SituationLineWeight
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
                reference.TextString = GetAttributeValue(definition.Tag, type, name, chainage, picket, note, length, diameter, material);
                blockReference.AttributeCollection.AppendAttribute(reference);
                transaction.AddNewlyCreatedDBObject(reference, true);
            }
        }

        private static string GetAttributeValue(
            string tag,
            string type,
            string name,
            double chainage,
            string picket,
            string note,
            double length,
            string diameter,
            string material)
        {
            switch (tag.ToUpperInvariant())
            {
                case AttrType:
                    return type;
                case AttrName:
                    return name;
                case AttrChainage:
                    return chainage.ToString("0.00", CultureInfo.InvariantCulture);
                case AttrPicket:
                    return picket;
                case AttrNote:
                    return note;
                case AttrLength:
                    return length > 0.0 ? length.ToString("0.00", CultureInfo.InvariantCulture) : "";
                case AttrDiameter:
                    return diameter;
                case AttrMaterial:
                    return material;
                default:
                    return "";
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
            if (layerTable.Has(LayerName))
            {
                var existing = (LayerTableRecord)transaction.GetObject(layerTable[LayerName], OpenMode.ForWrite);
                existing.Color = Color.FromColorIndex(ColorMethod.ByAci, 2);
                existing.LineWeight = SituationLineWeight;
                return;
            }

            layerTable.UpgradeOpen();
            var layer = new LayerTableRecord
            {
                Name = LayerName,
                Color = Color.FromColorIndex(ColorMethod.ByAci, 2),
                LineWeight = SituationLineWeight
            };

            layerTable.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
        }
    }
}
