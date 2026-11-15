// The few UnityEngine value types the canvas geometry of the core uses (ArchitectorGraphArranger, ArchitectorEdgeRouter),
// for the core without Unity. They repeat Unity's semantics: exact Equals and Unity's hash for dictionary keys, the
// approximate == of Vector2, Rect.Contains with an open upper edge, Mathf.Round to even. Unity compiles its own types;
// this file is only in Tools~/ArchitectorCore.
using System;

namespace UnityEngine
{
    public struct Vector2 : IEquatable<Vector2>
    {
        public const float kEpsilon = 0.00001f;
        public float x;
        public float y;

        public Vector2(float x, float y)
        {
            this.x = x;
            this.y = y;
        }

        public static Vector2 zero => new(0f, 0f);
        public static Vector2 one => new(1f, 1f);
        public static Vector2 up => new(0f, 1f);
        public static Vector2 down => new(0f, -1f);
        public static Vector2 left => new(-1f, 0f);
        public static Vector2 right => new(1f, 0f);

        public float sqrMagnitude => x * x + y * y;
        public float magnitude => (float)Math.Sqrt(x * x + y * y);

        public Vector2 normalized
        {
            get
            {
                var length = magnitude;
                return length > kEpsilon ? this / length : zero;
            }
        }

        public static float Dot(Vector2 a, Vector2 b) => a.x * b.x + a.y * b.y;
        public static float Distance(Vector2 a, Vector2 b) => (a - b).magnitude;

        public static Vector2 operator +(Vector2 a, Vector2 b) => new(a.x + b.x, a.y + b.y);
        public static Vector2 operator -(Vector2 a, Vector2 b) => new(a.x - b.x, a.y - b.y);
        public static Vector2 operator -(Vector2 a) => new(-a.x, -a.y);
        public static Vector2 operator *(Vector2 a, float d) => new(a.x * d, a.y * d);
        public static Vector2 operator *(float d, Vector2 a) => new(a.x * d, a.y * d);
        public static Vector2 operator /(Vector2 a, float d) => new(a.x / d, a.y / d);

        public static bool operator ==(Vector2 a, Vector2 b)
        {
            var dx = a.x - b.x;
            var dy = a.y - b.y;
            return dx * dx + dy * dy < kEpsilon * kEpsilon;
        }

        public static bool operator !=(Vector2 a, Vector2 b) => !(a == b);

        public bool Equals(Vector2 other) => x == other.x && y == other.y;
        public override bool Equals(object other) => other is Vector2 vector && Equals(vector);
        public override int GetHashCode() => x.GetHashCode() ^ (y.GetHashCode() << 2);
        public override string ToString() => $"({x:F2}, {y:F2})";
    }

    public struct Vector2Int : IEquatable<Vector2Int>
    {
        public int x;
        public int y;

        public Vector2Int(int x, int y)
        {
            this.x = x;
            this.y = y;
        }

        public static Vector2Int zero => new(0, 0);

        public static Vector2Int operator +(Vector2Int a, Vector2Int b) => new(a.x + b.x, a.y + b.y);
        public static Vector2Int operator -(Vector2Int a, Vector2Int b) => new(a.x - b.x, a.y - b.y);
        public static bool operator ==(Vector2Int a, Vector2Int b) => a.x == b.x && a.y == b.y;
        public static bool operator !=(Vector2Int a, Vector2Int b) => !(a == b);

        public bool Equals(Vector2Int other) => x == other.x && y == other.y;
        public override bool Equals(object other) => other is Vector2Int vector && Equals(vector);

        public override int GetHashCode()
        {
            var xHash = x.GetHashCode();
            var yHash = y.GetHashCode();
            return xHash ^ (yHash << 4) ^ (yHash >> 28) ^ (xHash >> 4) ^ (xHash << 28);
        }

        public override string ToString() => $"({x}, {y})";
    }

    public struct Rect : IEquatable<Rect>
    {
        private float m_XMin;
        private float m_YMin;
        private float m_Width;
        private float m_Height;

        public Rect(float x, float y, float width, float height)
        {
            m_XMin = x;
            m_YMin = y;
            m_Width = width;
            m_Height = height;
        }

        public Rect(Vector2 position, Vector2 size) : this(position.x, position.y, size.x, size.y)
        {
        }

        public static Rect MinMaxRect(float xmin, float ymin, float xmax, float ymax) => new(xmin, ymin, xmax - xmin, ymax - ymin);

        public float x { get => m_XMin; set => m_XMin = value; }
        public float y { get => m_YMin; set => m_YMin = value; }
        public float width { get => m_Width; set => m_Width = value; }
        public float height { get => m_Height; set => m_Height = value; }

        public Vector2 position
        {
            get => new(m_XMin, m_YMin);
            set
            {
                m_XMin = value.x;
                m_YMin = value.y;
            }
        }

        public Vector2 size
        {
            get => new(m_Width, m_Height);
            set
            {
                m_Width = value.x;
                m_Height = value.y;
            }
        }

        public Vector2 center
        {
            get => new(x + m_Width / 2f, y + m_Height / 2f);
            set
            {
                m_XMin = value.x - m_Width / 2f;
                m_YMin = value.y - m_Height / 2f;
            }
        }

        public Vector2 min => new(xMin, yMin);
        public Vector2 max => new(xMax, yMax);

        public float xMin
        {
            get => m_XMin;
            set
            {
                var oldXMax = xMax;
                m_XMin = value;
                m_Width = oldXMax - m_XMin;
            }
        }

        public float yMin
        {
            get => m_YMin;
            set
            {
                var oldYMax = yMax;
                m_YMin = value;
                m_Height = oldYMax - m_YMin;
            }
        }

        public float xMax
        {
            get => m_Width + m_XMin;
            set => m_Width = value - m_XMin;
        }

        public float yMax
        {
            get => m_Height + m_YMin;
            set => m_Height = value - m_YMin;
        }

        public bool Contains(Vector2 point) => point.x >= xMin && point.x < xMax && point.y >= yMin && point.y < yMax;

        public bool Overlaps(Rect other) => other.xMax > xMin && other.xMin < xMax && other.yMax > yMin && other.yMin < yMax;

        public static bool operator ==(Rect a, Rect b) => a.x == b.x && a.y == b.y && a.width == b.width && a.height == b.height;
        public static bool operator !=(Rect a, Rect b) => !(a == b);

        public bool Equals(Rect other) => x.Equals(other.x) && y.Equals(other.y) && width.Equals(other.width) && height.Equals(other.height);
        public override bool Equals(object other) => other is Rect rect && Equals(rect);
        public override int GetHashCode() => x.GetHashCode() ^ (width.GetHashCode() << 2) ^ (y.GetHashCode() >> 2) ^ (height.GetHashCode() >> 1);
    }

    public static class Mathf
    {
        public const float PI = (float)Math.PI;
        public const float Infinity = float.PositiveInfinity;
        public const float NegativeInfinity = float.NegativeInfinity;
        public static readonly float Epsilon = float.Epsilon;

        public static float Abs(float f) => Math.Abs(f);
        public static int Abs(int value) => Math.Abs(value);
        public static float Min(float a, float b) => a < b ? a : b;
        public static int Min(int a, int b) => a < b ? a : b;
        public static float Max(float a, float b) => a > b ? a : b;
        public static int Max(int a, int b) => a > b ? a : b;

        public static float Min(params float[] values)
        {
            if (values.Length == 0)
                return 0f;
            var result = values[0];
            for (var index = 1; index < values.Length; index++)
                if (values[index] < result)
                    result = values[index];
            return result;
        }

        public static float Max(params float[] values)
        {
            if (values.Length == 0)
                return 0f;
            var result = values[0];
            for (var index = 1; index < values.Length; index++)
                if (values[index] > result)
                    result = values[index];
            return result;
        }

        public static float Clamp(float value, float min, float max) => value < min ? min : value > max ? max : value;
        public static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;
        public static float Clamp01(float value) => value < 0f ? 0f : value > 1f ? 1f : value;
        public static float Sqrt(float f) => (float)Math.Sqrt(f);
        public static float Sin(float f) => (float)Math.Sin(f);
        public static float Cos(float f) => (float)Math.Cos(f);
        public static float Floor(float f) => (float)Math.Floor(f);
        public static float Ceil(float f) => (float)Math.Ceiling(f);
        public static float Round(float f) => (float)Math.Round(f);
        public static int FloorToInt(float f) => (int)Math.Floor(f);
        public static int CeilToInt(float f) => (int)Math.Ceiling(f);
        public static int RoundToInt(float f) => (int)Math.Round(f);
        public static float Sign(float f) => f >= 0f ? 1f : -1f;
        public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
        public static bool Approximately(float a, float b) =>
            Abs(b - a) < Max(0.000001f * Max(Abs(a), Abs(b)), Epsilon * 8f);
    }
}
