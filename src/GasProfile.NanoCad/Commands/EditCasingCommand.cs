using System;
using System.Collections.Generic;
using System.Globalization;
using GasProfile.Core.Profile;
using HostMgd.ApplicationServices;
using HostMgd.EditorInput;
using Teigha.DatabaseServices;
using Teigha.Geometry;
using Teigha.Runtime;

namespace GasProfile.NanoCad.Commands
{
    public sealed class EditCasingCommand
    {
        private const string GeneratedBlockName = "GASPROF_CASING_PLAN";
        private const string AttrDn = CasingAttributeNames.Dn;
        private const string AttrMaterial = CasingAttributeNames.Material;
        private const string AttrHasControlTube = CasingAttributeNames.HasControlTube;
        private const string AttrControlTubeChainage = CasingAttributeNames.ControlTubeChainage;
        private const string AttrControlTubePicket = CasingAttributeNames.ControlTubePicket;
        private const string AttrNormOffsetStart = CasingAttributeNames.NormOffsetStart;
        private const string AttrNormOffsetEnd = CasingAttributeNames.NormOffsetEnd;
        private const string AttrStartChainage = CasingAttributeNames.StartChainage;
        private const string AttrEndChainage = CasingAttributeNames.EndChainage;
        private const string AttrLength = CasingAttributeNames.Length;
        private const string AttrGeomSignature = CasingAttributeNames.GeomSignature;

        [CommandMethod("GASPROF_EDIT_CASING")]
        public void Run()
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                return;
            }

            var editor = document.Editor;
            var database = document.Database;

            var result = editor.GetEntity("\nВыберите блок футляра: ");
            if (result.Status != PromptStatus.OK)
            {
                return;
            }

            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var blockReference = transaction.GetObject(result.ObjectId, OpenMode.ForWrite, false) as BlockReference;
                if (blockReference == null)
                {
                    editor.WriteMessage("\nВыбранный объект не является вставкой блока.");
                    return;
                }

                var block = transaction.GetObject(blockReference.BlockTableRecord, OpenMode.ForRead, false) as BlockTableRecord;
                if (block == null)
                {
                    editor.WriteMessage("\nНе удалось прочитать определение блока.");
                    return;
                }

                var blockName = GetEffectiveBlockName(transaction, blockReference, block);
                var attrs = ReadAttributes(transaction, blockReference);
                if (!IsCasing(blockName, attrs))
                {
                    editor.WriteMessage("\nБлок не похож на футляр.");
                    return;
                }

                var oldDn = attrs.TryGetValue(AttrDn, out var dn) && !string.IsNullOrWhiteSpace(dn)
                    ? dn
                    : "Футляр";
                var newDn = GetString(editor, "\nПодпись футляра", oldDn);
                var oldMaterial = attrs.TryGetValue(AttrMaterial, out var material) && !string.IsNullOrWhiteSpace(material)
                    ? material
                    : CasingAttributeNames.DefaultMaterial;
                var newMaterial = GetString(editor, "\nМатериал футляра", oldMaterial);
                var oldHasControlTube = attrs.TryGetValue(AttrHasControlTube, out var hasControlTubeText)
                    ? IsYes(hasControlTubeText)
                    : true;
                var newHasControlTube = GetYesNo(editor, "\nКонтрольная трубка", oldHasControlTube);
                var oldNormOffsetStart = TryReadDouble(attrs, AttrNormOffsetStart, out var normOffsetStart)
                    ? normOffsetStart
                    : CasingAttributeNames.DefaultNormOffset;
                var oldNormOffsetEnd = TryReadDouble(attrs, AttrNormOffsetEnd, out var normOffsetEnd)
                    ? normOffsetEnd
                    : CasingAttributeNames.DefaultNormOffset;
                var newNormOffsetStart = GetDouble(editor, "\nНормативный отступ от начала футляра, м", oldNormOffsetStart);
                var newNormOffsetEnd = GetDouble(editor, "\nНормативный отступ от конца футляра, м", oldNormOffsetEnd);

                var canResize = string.Equals(blockName, GeneratedBlockName, StringComparison.OrdinalIgnoreCase);
                var oldLength = TryGetLength(attrs, blockReference, out var length)
                    ? length
                    : Math.Max(blockReference.ScaleFactors.X, 0.001);
                var newLength = canResize
                    ? GetDouble(editor, "\nДлина футляра, м", oldLength)
                    : oldLength;
                var oldControlTubeOffset = GetControlTubeOffset(attrs, oldLength);
                var newControlTubeOffset = newHasControlTube
                    ? GetDouble(editor, "\nСмещение контрольной трубки от начала футляра, м", Clamp(oldControlTubeOffset, 0.0, newLength))
                    : oldControlTubeOffset;

                UpdateAttribute(transaction, blockReference, AttrDn, newDn);
                UpdateAttribute(transaction, blockReference, AttrMaterial, newMaterial);
                UpdateAttribute(transaction, blockReference, AttrHasControlTube, newHasControlTube ? CasingAttributeNames.DefaultHasControlTube : "NO");
                UpdateAttribute(transaction, blockReference, AttrNormOffsetStart, FormatNumber(newNormOffsetStart));
                UpdateAttribute(transaction, blockReference, AttrNormOffsetEnd, FormatNumber(newNormOffsetEnd));
                UpdateControlTubeAttributes(transaction, blockReference, attrs, newHasControlTube, newControlTubeOffset);
                if (canResize)
                {
                    blockReference.ScaleFactors = new Scale3d(
                        Math.Max(newLength, 0.001),
                        blockReference.ScaleFactors.Y,
                        blockReference.ScaleFactors.Z);
                    NormalizeDnAttributeWidth(transaction, blockReference, newLength);
                    UpdateAttribute(transaction, blockReference, AttrLength, FormatNumber(newLength));
                }

                UpdateAttribute(transaction, blockReference, AttrGeomSignature, "");

                transaction.Commit();

                editor.WriteMessage("\n--- GASPROF_EDIT_CASING ---");
                editor.WriteMessage($"\nБлок: {blockName}");
                editor.WriteMessage($"\nПодпись: {newDn}");
                editor.WriteMessage($"\nМатериал: {newMaterial}");
                editor.WriteMessage($"\nКонтрольная трубка: {(newHasControlTube ? "Да" : "Нет")}");
                if (newHasControlTube)
                {
                    editor.WriteMessage($"\nСмещение КТ от начала: {FormatNumber(newControlTubeOffset)} м");
                }

                editor.WriteMessage($"\nНормативные отступы: {FormatNumber(newNormOffsetStart)} / {FormatNumber(newNormOffsetEnd)} м");
                if (canResize)
                {
                    editor.WriteMessage($"\nДлина: {FormatNumber(newLength)} м");
                }
                else
                {
                    editor.WriteMessage("\nДлина библиотечного/динамического блока меняется штатными ручками блока.");
                }

                editor.WriteMessage("\nGEOM_SIGNATURE очищен. Запустите GASPROF_RECALC_CASINGS или GREC для обновления пикетажа.");
                editor.WriteMessage("\n----------------------------");
            }
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

        private static bool IsCasing(string blockName, IReadOnlyDictionary<string, string> attrs)
        {
            if (!string.IsNullOrWhiteSpace(blockName) &&
                (blockName.IndexOf("Футляр", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 string.Equals(blockName, GeneratedBlockName, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return attrs.TryGetValue(AttrDn, out var dn) &&
                !string.IsNullOrWhiteSpace(dn) &&
                dn.IndexOf("Футляр", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool TryGetLength(
            IReadOnlyDictionary<string, string> attrs,
            BlockReference blockReference,
            out double length)
        {
            if (attrs.TryGetValue(AttrLength, out var text) &&
                double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out length) &&
                length > 0.0)
            {
                return true;
            }

            length = Math.Max(blockReference.ScaleFactors.X, 0.001);
            return length > 0.0;
        }

        private static double GetControlTubeOffset(IReadOnlyDictionary<string, string> attrs, double length)
        {
            if (CasingData.TryReadFromAttributes(string.Empty, attrs, out var casing))
            {
                return Clamp(casing.ControlTubeChainage - casing.StartChainage, 0.0, Math.Max(casing.Length, 0.001));
            }

            return Math.Max(0.0, length) * 0.78;
        }

        private static void UpdateControlTubeAttributes(
            Transaction transaction,
            BlockReference blockReference,
            IReadOnlyDictionary<string, string> attrs,
            bool hasControlTube,
            double controlTubeOffset)
        {
            if (!TryReadDouble(attrs, AttrStartChainage, out var start) ||
                !TryReadDouble(attrs, AttrEndChainage, out var end))
            {
                return;
            }

            if (end < start)
            {
                var swap = start;
                start = end;
                end = swap;
            }

            var length = Math.Max(end - start, 0.0);
            var controlTubeChainage = start + Clamp(controlTubeOffset, 0.0, length);
            UpdateAttribute(transaction, blockReference, AttrControlTubeChainage, FormatNumber(controlTubeChainage));
            UpdateAttribute(transaction, blockReference, AttrControlTubePicket, hasControlTube ? ChainageFormatter.Format(controlTubeChainage) : "");
        }

        private static bool TryReadDouble(IReadOnlyDictionary<string, string> attrs, string tag, out double value)
        {
            if (attrs.TryGetValue(tag, out var text) &&
                double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
                value >= 0.0)
            {
                return true;
            }

            value = 0.0;
            return false;
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

        private static void NormalizeDnAttributeWidth(
            Transaction transaction,
            BlockReference blockReference,
            double length)
        {
            foreach (ObjectId attributeId in blockReference.AttributeCollection)
            {
                var attribute = transaction.GetObject(attributeId, OpenMode.ForWrite, false) as AttributeReference;
                if (attribute == null || !string.Equals(attribute.Tag, AttrDn, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                attribute.WidthFactor = Math.Max(0.05, 1.0 / Math.Max(length, 1.0));
                return;
            }
        }

        private static string GetEffectiveBlockName(Transaction transaction, BlockReference blockReference, BlockTableRecord block)
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
            return IsYes(value);
        }

        private static bool IsYes(string value)
        {
            return value.Equals("Да", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("Д", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("YES", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("Y", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
        }

        private static double Clamp(double value, double min, double max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        private static string FormatNumber(double value)
        {
            return value.ToString("0.00", CultureInfo.InvariantCulture);
        }
    }
}
