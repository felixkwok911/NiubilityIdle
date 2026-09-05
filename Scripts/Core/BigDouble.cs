using System;
using Newtonsoft.Json;

namespace NiubilityIdle.Core
{
    // 移植自原游戏 Assembly-CSharp BigDouble，简化版支持 1e308+ 的增量
    [JsonObject]
    public struct BigDouble : IComparable<BigDouble>
    {
        public double mantissa;
        public long exponent;

        public BigDouble(double mantissa, long exponent)
        {
            this.mantissa = mantissa;
            this.exponent = exponent;
            Normalize();
        }

        public static BigDouble FromDouble(double v)
        {
            if (double.IsInfinity(v) || double.IsNaN(v) || v == 0) return new BigDouble(0, 0);
            long exp = (long)Math.Floor(Math.Log10(Math.Abs(v)));
            double man = v / Math.Pow(10, exp);
            return new BigDouble(man, exp);
        }

        void Normalize()
        {
            if (mantissa == 0) { exponent = 0; return; }
            while (Math.Abs(mantissa) >= 10) { mantissa /= 10; exponent++; }
            while (Math.Abs(mantissa) < 1) { mantissa *= 10; exponent--; }
        }

        public static BigDouble Zero => new BigDouble(0, 0);
        public static BigDouble One => new BigDouble(1, 0);

        public static BigDouble operator +(BigDouble a, BigDouble b)
        {
            if (a.exponent > b.exponent + 15) return a;
            if (b.exponent > a.exponent + 15) return b;
            // 对齐到较大指数
            if (a.exponent > b.exponent)
            {
                double bMan = b.mantissa * Math.Pow(10, b.exponent - a.exponent);
                return new BigDouble(a.mantissa + bMan, a.exponent);
            }
            else
            {
                double aMan = a.mantissa * Math.Pow(10, a.exponent - b.exponent);
                return new BigDouble(aMan + b.mantissa, b.exponent);
            }
        }

        public static BigDouble operator *(BigDouble a, double b) => new BigDouble(a.mantissa * b, a.exponent);
        public static BigDouble operator *(BigDouble a, BigDouble b) => new BigDouble(a.mantissa * b.mantissa, a.exponent + b.exponent);
        public static BigDouble operator -(BigDouble a, BigDouble b) => a + new BigDouble(-b.mantissa, b.exponent);

        public static bool operator >(BigDouble a, BigDouble b) => a.CompareTo(b) > 0;
        public static bool operator <(BigDouble a, BigDouble b) => a.CompareTo(b) < 0;
        public static bool operator >=(BigDouble a, BigDouble b) => a.CompareTo(b) >= 0;
        public static bool operator <=(BigDouble a, BigDouble b) => a.CompareTo(b) <= 0;

        public double ToDouble() => mantissa * Math.Pow(10, exponent);
        public int CompareTo(BigDouble other) => exponent != other.exponent ? exponent.CompareTo(other.exponent) : mantissa.CompareTo(other.mantissa);
        public override string ToString() => exponent < 6 ? ToDouble().ToString("F2") : $"{mantissa:F2}e{exponent}";
        public bool IsInfinity => exponent > 308;
    }
}
