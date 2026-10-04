using System;

namespace GasProfile.NanoCad.Commands
{
    internal enum SituationSymbolKind
    {
        RouteStart,
        RouteEnd,
        Bend,
        Picket,
        Crossing,
        Casing
    }

    internal sealed class SituationSymbol
    {
        public SituationSymbol(
            SituationSymbolKind kind,
            double chainage,
            string label,
            string picket,
            bool isSituation,
            double startChainage,
            double endChainage,
            double length,
            double? controlTubeChainage = null,
            bool hasControlTube = true,
            string name = "",
            string note = "")
        {
            Kind = kind;
            Chainage = chainage;
            Label = label ?? string.Empty;
            Picket = picket ?? string.Empty;
            IsSituation = isSituation;
            StartChainage = startChainage;
            EndChainage = endChainage;
            Length = length;
            ControlTubeChainage = controlTubeChainage;
            HasControlTube = hasControlTube;
            Name = name ?? string.Empty;
            Note = note ?? string.Empty;
        }

        public SituationSymbolKind Kind { get; }

        public double Chainage { get; }

        public string Label { get; }

        public string Picket { get; }

        public bool IsSituation { get; }

        public double StartChainage { get; }

        public double EndChainage { get; }

        public double Length { get; }

        public string Name { get; }

        public string Note { get; }

        public double? ControlTubeChainage { get; }

        public bool HasControlTube { get; }

        public bool IsCasing => Kind == SituationSymbolKind.Casing;

        public bool IsPicket => Kind == SituationSymbolKind.Picket;

        public static SituationSymbol Point(
            SituationSymbolKind kind,
            double chainage,
            string label,
            string picket,
            bool isSituation = false,
            string name = "")
        {
            return new SituationSymbol(kind, chainage, label, picket, isSituation, chainage, chainage, 0.0, name: name);
        }

        public static SituationSymbol Casing(
            double startChainage,
            double endChainage,
            string label,
            string startPicket,
            string endPicket,
            string name = "",
            double? controlTubeChainage = null,
            bool hasControlTube = true,
            string note = "")
        {
            var length = Math.Abs(endChainage - startChainage);
            var chainage = (startChainage + endChainage) * 0.5;
            return new SituationSymbol(
                SituationSymbolKind.Casing,
                chainage,
                label,
                $"{startPicket}-{endPicket}",
                true,
                startChainage,
                endChainage,
                length,
                controlTubeChainage,
                hasControlTube,
                name,
                note);
        }
    }
}
