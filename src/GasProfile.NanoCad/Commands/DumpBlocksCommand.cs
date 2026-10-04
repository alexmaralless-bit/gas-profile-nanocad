using System;
using System.Globalization;
using HostMgd.ApplicationServices;
using HostMgd.EditorInput;
using Teigha.DatabaseServices;
using Teigha.Geometry;
using Teigha.Runtime;

namespace GasProfile.NanoCad.Commands
{
    public sealed class DumpBlocksCommand
    {
        [CommandMethod("GASPROF_DUMP_BLOCKS")]
        public void DumpBlocks()
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                return;
            }

            var editor = document.Editor;
            var database = document.Database;

            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
                editor.WriteMessage("\n--- GASPROF_DUMP_BLOCKS ---");

                foreach (ObjectId blockId in blockTable)
                {
                    var block = transaction.GetObject(blockId, OpenMode.ForRead, false) as BlockTableRecord;
                    if (block == null || block.IsAnonymous || block.IsLayout)
                    {
                        continue;
                    }

                    var entityCount = 0;
                    var attributeCount = 0;
                    foreach (ObjectId entityId in block)
                    {
                        entityCount++;
                        if (transaction.GetObject(entityId, OpenMode.ForRead, false) is AttributeDefinition)
                        {
                            attributeCount++;
                        }
                    }

                    editor.WriteMessage($"\n{block.Name}: entities={entityCount}, attrdefs={attributeCount}");
                }

                editor.WriteMessage("\n---------------------------");
                transaction.Commit();
            }
        }

        [CommandMethod("GASPROF_DUMP_BLOCK")]
        public void DumpBlock()
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                return;
            }

            var editor = document.Editor;
            var database = document.Database;

            var result = editor.GetEntity("\nВыберите вставку блока для диагностики: ");
            if (result.Status != PromptStatus.OK)
            {
                return;
            }

            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var reference = transaction.GetObject(result.ObjectId, OpenMode.ForRead, false) as BlockReference;
                if (reference == null)
                {
                    editor.WriteMessage("\nВыбранный объект не является BlockReference.");
                    return;
                }

                var block = transaction.GetObject(reference.BlockTableRecord, OpenMode.ForRead, false) as BlockTableRecord;
                if (block == null)
                {
                    editor.WriteMessage("\nНе удалось прочитать определение блока.");
                    return;
                }

                editor.WriteMessage("\n--- GASPROF_DUMP_BLOCK ---");
                editor.WriteMessage($"\nBlock: {block.Name}");
                editor.WriteMessage($"\nEffective block: {GetEffectiveBlockName(transaction, reference, block)}");
                editor.WriteMessage($"\nLayer: {reference.Layer}");
                editor.WriteMessage($"\nPosition: {FormatPoint(reference.Position)}");
                editor.WriteMessage($"\nRotation: {reference.Rotation.ToString("0.######", CultureInfo.InvariantCulture)}");
                editor.WriteMessage($"\nScale: X={reference.ScaleFactors.X.ToString("0.######", CultureInfo.InvariantCulture)} Y={reference.ScaleFactors.Y.ToString("0.######", CultureInfo.InvariantCulture)} Z={reference.ScaleFactors.Z.ToString("0.######", CultureInfo.InvariantCulture)}");

                editor.WriteMessage("\nAttributes:");
                var hasAttributes = false;
                foreach (ObjectId attributeId in reference.AttributeCollection)
                {
                    var attribute = transaction.GetObject(attributeId, OpenMode.ForRead, false) as AttributeReference;
                    if (attribute == null)
                    {
                        continue;
                    }

                    hasAttributes = true;
                    editor.WriteMessage($"\n  {attribute.Tag} = {attribute.TextString}");
                }

                if (!hasAttributes)
                {
                    editor.WriteMessage("\n  <none>");
                }

                editor.WriteMessage("\nDefinition entities:");
                foreach (ObjectId entityId in block)
                {
                    var entity = transaction.GetObject(entityId, OpenMode.ForRead, false) as Entity;
                    if (entity == null)
                    {
                        continue;
                    }

                    editor.WriteMessage($"\n  {DescribeEntity(entity)}");
                }

                editor.WriteMessage("\n--------------------------");
                transaction.Commit();
            }
        }

        private static string DescribeEntity(Entity entity)
        {
            if (entity is AttributeDefinition attribute)
            {
                return $"AttributeDefinition tag={attribute.Tag} invisible={attribute.Invisible}";
            }

            if (entity is Line line)
            {
                return $"Line layer={line.Layer} start={FormatPoint(line.StartPoint)} end={FormatPoint(line.EndPoint)}";
            }

            if (entity is Circle circle)
            {
                return $"Circle layer={circle.Layer} center={FormatPoint(circle.Center)} radius={circle.Radius.ToString("0.######", CultureInfo.InvariantCulture)}";
            }

            if (entity is Arc arc)
            {
                return $"Arc layer={arc.Layer} center={FormatPoint(arc.Center)} radius={arc.Radius.ToString("0.######", CultureInfo.InvariantCulture)}";
            }

            if (entity is Polyline polyline)
            {
                return $"Polyline layer={polyline.Layer} vertices={polyline.NumberOfVertices} closed={polyline.Closed}";
            }

            if (entity is DBText text)
            {
                return $"DBText layer={text.Layer} text={text.TextString}";
            }

            if (entity is MText mtext)
            {
                return $"MText layer={mtext.Layer} text={mtext.Contents}";
            }

            return $"{entity.GetType().Name} layer={entity.Layer}";
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

        private static string FormatPoint(Point3d point)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "({0:0.###}, {1:0.###}, {2:0.###})",
                point.X,
                point.Y,
                point.Z);
        }
    }
}
