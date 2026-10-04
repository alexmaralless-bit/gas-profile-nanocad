namespace GasProfile.Core.Geometry
{
    public readonly struct Segment2
    {
        public Segment2(Point2 start, Point2 end)
        {
            Start = start;
            End = end;
        }

        public Point2 Start { get; }

        public Point2 End { get; }

        public double Length => Start.DistanceTo(End);
    }
}
