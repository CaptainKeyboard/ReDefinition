using System;

namespace ReDefinition.Orbits
{
    // A vector of three doubles in a right-handed frame, with what the orbit maths needs. Free of Unity and KSP, so that the maths is
    // tested outside the game; the KSP side converts (Orbits/KspOrbits).
    internal struct V3
    {
        public readonly double X, Y, Z;

        public V3(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static V3 operator +(V3 a, V3 b) { return new V3(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
        public static V3 operator -(V3 a, V3 b) { return new V3(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
        public static V3 operator -(V3 a) { return new V3(-a.X, -a.Y, -a.Z); }
        public static V3 operator *(V3 a, double s) { return new V3(a.X * s, a.Y * s, a.Z * s); }
        public static V3 operator *(double s, V3 a) { return a * s; }
        public static V3 operator /(V3 a, double s) { return new V3(a.X / s, a.Y / s, a.Z / s); }

        public double Length
        {
            get { return Math.Sqrt(X * X + Y * Y + Z * Z); }
        }

        // The unit vector, or the zero vector where this is zero.
        public V3 Unit
        {
            get
            {
                double length = Length;
                return length > 0.0 ? this / length : new V3(0.0, 0.0, 0.0);
            }
        }

        public static double Dot(V3 a, V3 b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; }

        public static V3 Cross(V3 a, V3 b)
        {
            return new V3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        }

        // The angle between two vectors, 0 to pi.
        public static double Angle(V3 a, V3 b)
        {
            double cos = Dot(a, b) / (a.Length * b.Length);
            return Math.Acos(Math.Max(-1.0, Math.Min(1.0, cos)));
        }

        // This vector turned by `angle` radians about the axis (Rodrigues).
        public V3 Rotated(V3 axis, double angle)
        {
            V3 k = axis.Unit;
            double cos = Math.Cos(angle), sin = Math.Sin(angle);
            return this * cos + Cross(k, this) * sin + k * (Dot(k, this) * (1.0 - cos));
        }

        public override string ToString()
        {
            return "(" + X.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + ", "
                   + Y.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + ", "
                   + Z.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + ")";
        }
    }

    // A position and a velocity: the state of a body on its orbit.
    internal struct State
    {
        public readonly V3 R, V;

        public State(V3 r, V3 v)
        {
            R = r;
            V = v;
        }
    }
}
