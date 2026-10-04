using System;
using System.Collections.Generic;
using System.Globalization;
using GasProfile.Core.Profile;

namespace GasProfile.NanoCad.Commands
{
    internal sealed class CasingData
    {
        public CasingData(
            string blockName,
            string dn,
            string name,
            string material,
            bool hasControlTube,
            double normOffsetStart,
            double normOffsetEnd,
            double startChainage,
            double endChainage,
            double? controlTubeChainage = null)
        {
            BlockName = blockName ?? string.Empty;
            Dn = string.IsNullOrWhiteSpace(dn) ? "Футляр" : dn.Trim();
            Name = string.IsNullOrWhiteSpace(name) ? Dn : name.Trim();
            Material = string.IsNullOrWhiteSpace(material) ? CasingAttributeNames.DefaultMaterial : material.Trim();
            HasControlTube = hasControlTube;
            NormOffsetStart = normOffsetStart;
            NormOffsetEnd = normOffsetEnd;

            if (endChainage < startChainage)
            {
                var swap = startChainage;
                startChainage = endChainage;
                endChainage = swap;
            }

            StartChainage = startChainage;
            EndChainage = endChainage;
            Length = Math.Abs(endChainage - startChainage);
            CenterChainage = (startChainage + endChainage) * 0.5;
            ControlTubeChainage = controlTubeChainage ?? startChainage + Length * 0.78;
        }

        public string BlockName { get; }

        public string Dn { get; }

        public string Name { get; }

        public string Material { get; }

        public bool HasControlTube { get; }

        public double NormOffsetStart { get; }

        public double NormOffsetEnd { get; }

        public double StartChainage { get; }

        public double EndChainage { get; }

        public double CenterChainage { get; }

        public double Length { get; }

        public double ControlTubeChainage { get; }

        public string StartPicket => ChainageFormatter.Format(StartChainage);

        public string EndPicket => ChainageFormatter.Format(EndChainage);

        public string ControlTubePicket => ChainageFormatter.Format(ControlTubeChainage);

        public string PicketRange => $"{StartPicket}-{EndPicket}";

        public string UnfoldedPlanLabel => $"{Dn}; {Material}; L={FormatDisplayNumber(Length)}";

        public string NormOffsetPlanLabel => $"отст. {NormOffsetLabel} м";

        public string ProfileLabel => HasControlTube ? "Футляр, КТ" : "Футляр";

        public string NormOffsetLabel => $"{FormatDisplayNumber(NormOffsetStart)}/{FormatDisplayNumber(NormOffsetEnd)}";

        public static bool HasStructuredParameters(IReadOnlyDictionary<string, string> attrs)
        {
            return attrs.TryGetValue(CasingAttributeNames.Material, out var material) &&
                !string.IsNullOrWhiteSpace(material) &&
                attrs.TryGetValue(CasingAttributeNames.HasControlTube, out var hasControlTube) &&
                !string.IsNullOrWhiteSpace(hasControlTube) &&
                attrs.TryGetValue(CasingAttributeNames.NormOffsetStart, out var normOffsetStart) &&
                !string.IsNullOrWhiteSpace(normOffsetStart) &&
                attrs.TryGetValue(CasingAttributeNames.NormOffsetEnd, out var normOffsetEnd) &&
                !string.IsNullOrWhiteSpace(normOffsetEnd);
        }

        public static CasingData FromAttributes(
            string blockName,
            IReadOnlyDictionary<string, string> attrs,
            double startChainage,
            double endChainage)
        {
            attrs.TryGetValue(CasingAttributeNames.Dn, out var dn);
            attrs.TryGetValue("2_ИМЯ", out var name);
            attrs.TryGetValue(CasingAttributeNames.Material, out var material);
            var hasControlTube = !attrs.TryGetValue(CasingAttributeNames.HasControlTube, out var hasControlTubeText) ||
                IsYes(hasControlTubeText);
            var normOffsetStart = ReadDouble(attrs, CasingAttributeNames.NormOffsetStart, CasingAttributeNames.DefaultNormOffset);
            var normOffsetEnd = ReadDouble(attrs, CasingAttributeNames.NormOffsetEnd, CasingAttributeNames.DefaultNormOffset);
            var controlTubeChainage = TryReadDouble(attrs, CasingAttributeNames.ControlTubeChainage, out var control)
                ? control
                : (double?)null;

            return new CasingData(
                blockName,
                string.IsNullOrWhiteSpace(dn) ? "Футляр" : dn,
                string.IsNullOrWhiteSpace(name) ? string.Empty : name,
                string.IsNullOrWhiteSpace(material) ? CasingAttributeNames.DefaultMaterial : material,
                hasControlTube,
                normOffsetStart,
                normOffsetEnd,
                startChainage,
                endChainage,
                controlTubeChainage);
        }

        public static bool TryReadFromAttributes(
            string blockName,
            IReadOnlyDictionary<string, string> attrs,
            out CasingData data)
        {
            data = null!;
            if (!TryReadDouble(attrs, CasingAttributeNames.StartChainage, out var start) ||
                !TryReadDouble(attrs, CasingAttributeNames.EndChainage, out var end) ||
                Math.Abs(end - start) < 0.001)
            {
                return false;
            }

            data = FromAttributes(blockName, attrs, start, end);
            return true;
        }

        private static double ReadDouble(IReadOnlyDictionary<string, string> attrs, string tag, double defaultValue)
        {
            return TryReadDouble(attrs, tag, out var value) ? value : defaultValue;
        }

        internal static bool TryReadDouble(IReadOnlyDictionary<string, string> attrs, string tag, out double value)
        {
            if (attrs.TryGetValue(tag, out var text) &&
                double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }

            value = 0.0;
            return false;
        }

        internal static bool IsYes(string value)
        {
            return !value.Equals("NO", StringComparison.OrdinalIgnoreCase) &&
                !value.Equals("Нет", StringComparison.OrdinalIgnoreCase) &&
                !value.Equals("Н", StringComparison.OrdinalIgnoreCase) &&
                !value.Equals("0", StringComparison.OrdinalIgnoreCase) &&
                !value.Equals("False", StringComparison.OrdinalIgnoreCase);
        }

        private static string FormatDisplayNumber(double value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture).Replace('.', ',');
        }
    }
}
