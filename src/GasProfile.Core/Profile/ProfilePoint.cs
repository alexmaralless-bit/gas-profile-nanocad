using GasProfile.Core.Geometry;

namespace GasProfile.Core.Profile
{
    public sealed class ProfilePoint
    {
        public ProfilePoint(double chainage, Point2 planPoint, ProfilePointType type, double? groundElevation)
        {
            Chainage = chainage;
            PlanPoint = planPoint;
            Type = type;
            GroundElevation = groundElevation;
        }

        public double Chainage { get; }

        public Point2 PlanPoint { get; }

        public ProfilePointType Type { get; }

        public double? GroundElevation { get; }
    }
}
