using System;

namespace AtmoSim.Core
{
    // Deterministic elementary functions built from + - * / and Math.Sqrt / Math.Floor only. Those operations are exactly
    // rounded by IEEE 754, so the results do not depend on the platform's libm (.NET or Mono). The approximations are
    // accurate to about 1e-13 relative, which is all the simulation needs; reproducibility is the point.
    public static class DMath
    {
        public const double Pi = 3.14159265358979323846;
        public const double TwoPi = 6.28318530717958647692;
        public const double HalfPi = 1.57079632679489661923;
        private const double Ln2 = 0.693147180559945309417;
        private const double InvLn2 = 1.44269504088896340736;

        public static double Sqrt(double x) { return Math.Sqrt(x); }
        public static double Floor(double x) { return Math.Floor(x); }
        public static double Abs(double x) { return x < 0 ? -x : x; }
        public static double Min(double a, double b) { return a < b ? a : b; }
        public static double Max(double a, double b) { return a > b ? a : b; }
        public static double Clamp(double x, double lo, double hi) { return x < lo ? lo : (x > hi ? hi : x); }

        // 2^n for an integer n by repeated squaring of 2 or 0.5 (every product is a power of two, so it is exact).
        private static double Pow2(int n)
        {
            // the bit pattern of 2^n for -1022 <= n <= 1023 is exact; Exp limits its argument to +-700 so n stays in range
            return BitConverter.Int64BitsToDouble((long)(n + 1023) << 52);
        }

        public static double Exp(double x)
        {
            if (double.IsNaN(x)) return x;
            if (x > 700.0) x = 700.0;
            if (x < -700.0) return 0.0;
            double kf = Math.Floor(x * InvLn2 + 0.5);
            double r = x - kf * Ln2;
            double t = 1.0 + r / 12.0;
            t = 1.0 + r / 11.0 * t;
            t = 1.0 + r / 10.0 * t;
            t = 1.0 + r / 9.0 * t;
            t = 1.0 + r / 8.0 * t;
            t = 1.0 + r / 7.0 * t;
            t = 1.0 + r / 6.0 * t;
            t = 1.0 + r / 5.0 * t;
            t = 1.0 + r / 4.0 * t;
            t = 1.0 + r / 3.0 * t;
            t = 1.0 + r / 2.0 * t;
            t = 1.0 + r * t;
            return t * Pow2((int)kf);
        }

        public static double Log(double x)
        {
            if (!(x > 0.0)) return x == 0.0 ? -1e300 : double.NaN;
            int k = 0;
            while (x >= 2.0) { x *= 0.5; k++; }
            while (x < 1.0) { x *= 2.0; k--; }
            if (x > 1.4142135623730951) { x *= 0.5; k++; }
            double z = (x - 1.0) / (x + 1.0);
            double z2 = z * z;
            double s = 1.0 / 25.0;
            s = 1.0 / 23.0 + z2 * s;
            s = 1.0 / 21.0 + z2 * s;
            s = 1.0 / 19.0 + z2 * s;
            s = 1.0 / 17.0 + z2 * s;
            s = 1.0 / 15.0 + z2 * s;
            s = 1.0 / 13.0 + z2 * s;
            s = 1.0 / 11.0 + z2 * s;
            s = 1.0 / 9.0 + z2 * s;
            s = 1.0 / 7.0 + z2 * s;
            s = 1.0 / 5.0 + z2 * s;
            s = 1.0 / 3.0 + z2 * s;
            s = 1.0 + z2 * s;
            return 2.0 * z * s + k * Ln2;
        }

        public static double Pow(double x, double y)
        {
            if (y == 0.0) return 1.0;
            if (x <= 0.0) return x == 0.0 ? 0.0 : double.NaN;
            return Exp(y * Log(x));
        }

        public static double Sin(double x)
        {
            double k = Math.Floor(x / TwoPi + 0.5);
            double r = x - k * TwoPi;
            if (r > HalfPi) r = Pi - r;
            else if (r < -HalfPi) r = -Pi - r;
            double r2 = r * r;
            double s = 1.0 / 355687428096000.0; // 1/17!
            s = -1.0 / 1307674368000.0 + r2 * s; // -1/15!
            s = 1.0 / 6227020800.0 + r2 * s;     // 1/13!
            s = -1.0 / 39916800.0 + r2 * s;      // -1/11!
            s = 1.0 / 362880.0 + r2 * s;         // 1/9!
            s = -1.0 / 5040.0 + r2 * s;          // -1/7!
            s = 1.0 / 120.0 + r2 * s;            // 1/5!
            s = -1.0 / 6.0 + r2 * s;             // -1/3!
            s = 1.0 + r2 * s;
            return r * s;
        }

        public static double Cos(double x) { return Sin(x + HalfPi); }

        // Arc tangent: range reduction to |t| <= tan(pi/12), then the odd series.
        public static double Atan(double x)
        {
            bool neg = x < 0.0;
            if (neg) x = -x;
            bool inv = x > 1.0;
            if (inv) x = 1.0 / x;
            // atan(x) = pi/6 + atan((x*sqrt3 - 1)/(sqrt3 + x)) shifts [0.268, 1] toward zero
            const double Sqrt3 = 1.7320508075688772935;
            bool shifted = x > 0.2679491924311227;
            double t = shifted ? (x * Sqrt3 - 1.0) / (Sqrt3 + x) : x;
            double t2 = t * t;
            double a;
            a = t * AtanSeries(t2);
            if (shifted) a += Pi / 6.0;
            if (inv) a = HalfPi - a;
            return neg ? -a : a;
        }

        private static double AtanSeries(double t2)
        {
            // 1 - t2/3 + t2^2/5 - ... (16 terms), |t| <= 0.268 so t2 <= 0.072 and the tail is below 1e-19
            double s = -1.0 / 31.0;
            s = 1.0 / 29.0 + t2 * s;
            s = -1.0 / 27.0 + t2 * s;
            s = 1.0 / 25.0 + t2 * s;
            s = -1.0 / 23.0 + t2 * s;
            s = 1.0 / 21.0 + t2 * s;
            s = -1.0 / 19.0 + t2 * s;
            s = 1.0 / 17.0 + t2 * s;
            s = -1.0 / 15.0 + t2 * s;
            s = 1.0 / 13.0 + t2 * s;
            s = -1.0 / 11.0 + t2 * s;
            s = 1.0 / 9.0 + t2 * s;
            s = -1.0 / 7.0 + t2 * s;
            s = 1.0 / 5.0 + t2 * s;
            s = -1.0 / 3.0 + t2 * s;
            s = 1.0 + t2 * s;
            return s;
        }

        public static double Atan2(double y, double x)
        {
            if (x > 0.0) return Atan(y / x);
            if (x < 0.0) return y >= 0.0 ? Atan(y / x) + Pi : Atan(y / x) - Pi;
            if (y > 0.0) return HalfPi;
            if (y < 0.0) return -HalfPi;
            return 0.0;
        }

        public static double Asin(double x)
        {
            x = Clamp(x, -1.0, 1.0);
            return Atan2(x, Math.Sqrt(1.0 - x * x));
        }

        public static double Acos(double x) { return HalfPi - Asin(x); }

        // tanh(x) = 1 - 2 / (exp(2x) + 1); saturates beyond |x| = 20.
        public static double Tanh(double x)
        {
            if (x > 20.0) return 1.0;
            if (x < -20.0) return -1.0;
            return 1.0 - 2.0 / (Exp(2.0 * x) + 1.0);
        }

        // A Pade form of tanh, exact at 0, saturated beyond |x| = 3, error below 0.02: used where only the shape of a step matters.
        public static double TanhFast(double x)
        {
            if (x >= 3.0) return 1.0;
            if (x <= -3.0) return -1.0;
            double x2 = x * x;
            return x * (27.0 + x2) / (27.0 + 9.0 * x2);
        }

        // 0 below a, 1 above b, cubic in between.
        public static double SmoothStep(double a, double b, double x)
        {
            double t = (x - a) / (b - a);
            if (t <= 0.0) return 0.0;
            if (t >= 1.0) return 1.0;
            return t * t * (3.0 - 2.0 * t);
        }

        // Wraps an angle into [-pi, pi).
        public static double WrapPi(double x)
        {
            return x - TwoPi * Math.Floor((x + Pi) / TwoPi);
        }

        // Fraction part in [0, 1).
        public static double Frac(double x) { return x - Math.Floor(x); }
    }
}
