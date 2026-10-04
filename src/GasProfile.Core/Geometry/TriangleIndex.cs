namespace GasProfile.Core.Geometry
{
    public readonly struct TriangleIndex
    {
        public TriangleIndex(int a, int b, int c)
        {
            A = a;
            B = b;
            C = c;
        }

        public int A { get; }

        public int B { get; }

        public int C { get; }

        public Edge2 EdgeAB => new Edge2(A, B);

        public Edge2 EdgeBC => new Edge2(B, C);

        public Edge2 EdgeCA => new Edge2(C, A);
    }
}
