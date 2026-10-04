using System;

namespace GasProfile.Core.Geometry
{
    public readonly struct Edge2 : IEquatable<Edge2>
    {
        public Edge2(int a, int b)
        {
            if (a < b)
            {
                A = a;
                B = b;
            }
            else
            {
                A = b;
                B = a;
            }
        }

        public int A { get; }

        public int B { get; }

        public bool Equals(Edge2 other)
        {
            return A == other.A && B == other.B;
        }

        public override bool Equals(object? obj)
        {
            return obj is Edge2 other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (A * 397) ^ B;
            }
        }
    }
}
