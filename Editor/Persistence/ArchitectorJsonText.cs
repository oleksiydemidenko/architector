using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Architector.Editor
{
    // Unity's JsonUtility without Unity, for the core outside the Editor. It writes the same text as
    // JsonUtility.ToJson(value, true) for the Architector documents: public instance fields in declaration order (base
    // class first), 4-space indentation, a null string as "", a null list as [], a null [Serializable] object as its
    // defaults, a float as the shortest text of its double value with ".0" when whole. Reading keeps the field
    // initializers for absent fields and ignores unknown fields, as FromJson does.
    public static class ArchitectorJsonText
    {
        private static readonly ConcurrentDictionary<Type, FieldInfo[]> FieldsByType = new();

        public static string ToJson(object value)
        {
            if (value == null)
                return string.Empty;
            var builder = new StringBuilder();
            WriteObject(builder, value, 0);
            return builder.ToString();
        }

        public static T FromJson<T>(string json) => (T)FromJson(json, typeof(T));

        public static object FromJson(string json, Type type)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentException("JSON parse error: The document is empty.");
            JToken token;
            using (var reader = new JsonTextReader(new System.IO.StringReader(json))
                   {
                       DateParseHandling = DateParseHandling.None,
                       FloatParseHandling = FloatParseHandling.Double
                   })
            {
                token = JToken.ReadFrom(reader);
                if (reader.Read() && reader.TokenType != JsonToken.Comment)
                    throw new ArgumentException("JSON parse error: The document root must not be followed by other values.");
            }
            if (token is not JObject root)
                throw new ArgumentException("JSON must represent an object type.");
            return ReadObject(root, type);
        }

        private static FieldInfo[] Fields(Type type) => FieldsByType.GetOrAdd(type, candidate =>
        {
            var chain = new List<Type>();
            for (var current = candidate; current != null && current != typeof(object); current = current.BaseType)
                chain.Insert(0, current);
            return chain.SelectMany(current => current
                    .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Where(IsSerialized)
                    .OrderBy(field => field.MetadataToken))
                .ToArray();
        });

        private static bool IsSerialized(FieldInfo field)
        {
            if (field.IsInitOnly || field.IsLiteral || (field.Attributes & FieldAttributes.NotSerialized) != 0)
                return false;
            if (!field.IsPublic && !field.GetCustomAttributes(false).Any(attribute => attribute.GetType().Name == "SerializeField"))
                return false;
            return IsSupported(field.FieldType);
        }

        private static bool IsSupported(Type type)
        {
            if (IsScalar(type))
                return true;
            var element = ElementType(type);
            if (element != null)
                return ElementType(element) == null && IsSupported(element);
            return IsSerializableObject(type);
        }

        private static bool IsScalar(Type type) =>
            type.IsPrimitive && type != typeof(IntPtr) && type != typeof(UIntPtr) || type == typeof(string) || type.IsEnum;

        private static bool IsSerializableObject(Type type) =>
            !type.IsAbstract && !type.IsInterface && !type.IsGenericType && type.IsDefined(typeof(SerializableAttribute), false)
            && (type.IsValueType || type.GetConstructor(Type.EmptyTypes) != null);

        private static Type ElementType(Type type)
        {
            if (type.IsArray && type.GetArrayRank() == 1)
                return type.GetElementType();
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
                return type.GetGenericArguments()[0];
            return null;
        }

        private static void Indent(StringBuilder builder, int depth) => builder.Append(' ', depth * 4);

        private static void WriteObject(StringBuilder builder, object value, int depth)
        {
            var fields = Fields(value.GetType());
            if (fields.Length == 0)
            {
                builder.Append("{}");
                return;
            }
            builder.Append("{\n");
            for (var index = 0; index < fields.Length; index++)
            {
                var field = fields[index];
                Indent(builder, depth + 1);
                WriteString(builder, field.Name);
                builder.Append(": ");
                WriteValue(builder, field.FieldType, field.GetValue(value), depth + 1);
                if (index < fields.Length - 1)
                    builder.Append(',');
                builder.Append('\n');
            }
            Indent(builder, depth);
            builder.Append('}');
        }

        private static void WriteValue(StringBuilder builder, Type type, object value, int depth)
        {
            if (type == typeof(string))
            {
                WriteString(builder, (string)value ?? string.Empty);
                return;
            }
            if (type.IsEnum)
            {
                builder.Append(Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                return;
            }
            if (type == typeof(bool))
            {
                builder.Append((bool)value ? "true" : "false");
                return;
            }
            if (type == typeof(float))
            {
                builder.Append(FormatReal((float)value));
                return;
            }
            if (type == typeof(double))
            {
                builder.Append(FormatReal((double)value));
                return;
            }
            if (type.IsPrimitive)
            {
                builder.Append(type == typeof(char)
                    ? ((int)(char)value).ToString(CultureInfo.InvariantCulture)
                    : Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            }
            var element = ElementType(type);
            if (element != null)
            {
                var items = value == null ? Array.Empty<object>() : ((IEnumerable)value).Cast<object>().ToArray();
                if (items.Length == 0)
                {
                    builder.Append("[]");
                    return;
                }
                builder.Append("[\n");
                for (var index = 0; index < items.Length; index++)
                {
                    Indent(builder, depth + 1);
                    WriteValue(builder, element, items[index], depth + 1);
                    if (index < items.Length - 1)
                        builder.Append(',');
                    builder.Append('\n');
                }
                Indent(builder, depth);
                builder.Append(']');
                return;
            }
            WriteObject(builder, value ?? Activator.CreateInstance(type), depth);
        }

        // JsonUtility writes a number with rapidjson's dtoa: Grisu2 digits of the double value, then its Prettify layout
        // (750.0, 0.25, 0.001234, 1.5e30). Grisu2 does not always give the shortest digits, and its last digit can differ
        // from .NET's "R" (0.055f is 0.054999999701976779 here, 0.054999999701976776 in .NET), so it is repeated here.
        public static string FormatReal(double value)
        {
            if (double.IsNaN(value))
                return "NaN";
            if (double.IsPositiveInfinity(value))
                return "Infinity";
            if (double.IsNegativeInfinity(value))
                return "-Infinity";
            var bits = BitConverter.DoubleToInt64Bits(value);
            if (value == 0d)
                return bits < 0 ? "-0.0" : "0.0";
            var buffer = new char[40];
            var negative = value < 0;
            Grisu2.Digits(Math.Abs(value), buffer, out var length, out var k);
            return (negative ? "-" : string.Empty) + Grisu2.Prettify(buffer, length, k);
        }

        // rapidjson's internal/dtoa.h and diyfp.h (Milo Yip), MIT license.
        private static class Grisu2
        {
            private const int SignificandSize = 52;
            private const int ExponentBias = 0x3FF + SignificandSize;
            private const int MinExponent = -ExponentBias;
            private const ulong ExponentMask = 0x7FF0000000000000UL;
            private const ulong SignificandMask = 0x000FFFFFFFFFFFFFUL;
            private const ulong HiddenBit = 0x0010000000000000UL;

            private static readonly ulong[] Pow10 =
            {
                1UL, 10UL, 100UL, 1000UL, 10000UL, 100000UL, 1000000UL, 10000000UL, 100000000UL, 1000000000UL,
                10000000000UL, 100000000000UL, 1000000000000UL, 10000000000000UL, 100000000000000UL,
                1000000000000000UL, 10000000000000000UL, 100000000000000000UL, 1000000000000000000UL,
                10000000000000000000UL
            };

            private static readonly (ulong F, int E)[] CachedPowers = BuildCachedPowers();

            private readonly struct DiyFp
            {
                public readonly ulong F;
                public readonly int E;

                public DiyFp(ulong f, int e)
                {
                    F = f;
                    E = e;
                }

                public static DiyFp Of(double value)
                {
                    var bits = (ulong)BitConverter.DoubleToInt64Bits(value);
                    var biased = (int)((bits & ExponentMask) >> SignificandSize);
                    var significand = bits & SignificandMask;
                    return biased != 0
                        ? new DiyFp(significand + HiddenBit, biased - ExponentBias)
                        : new DiyFp(significand, MinExponent + 1);
                }

                public static DiyFp operator -(DiyFp a, DiyFp b) => new(a.F - b.F, a.E);

                public static DiyFp operator *(DiyFp x, DiyFp y)
                {
                    const ulong m32 = 0xFFFFFFFFUL;
                    var a = x.F >> 32;
                    var b = x.F & m32;
                    var c = y.F >> 32;
                    var d = y.F & m32;
                    var ac = a * c;
                    var bc = b * c;
                    var ad = a * d;
                    var bd = b * d;
                    var tmp = (bd >> 32) + (ad & m32) + (bc & m32);
                    tmp += 1UL << 31;
                    return new DiyFp(ac + (ad >> 32) + (bc >> 32) + (tmp >> 32), x.E + y.E + 64);
                }

                public DiyFp Normalize()
                {
                    var f = F;
                    var e = E;
                    while ((f & (1UL << 63)) == 0)
                    {
                        f <<= 1;
                        e--;
                    }
                    return new DiyFp(f, e);
                }

                private DiyFp NormalizeBoundary()
                {
                    var f = F;
                    var e = E;
                    while ((f & (HiddenBit << 1)) == 0)
                    {
                        f <<= 1;
                        e--;
                    }
                    const int shift = 64 - SignificandSize - 2;
                    return new DiyFp(f << shift, e - shift);
                }

                public void NormalizedBoundaries(out DiyFp minus, out DiyFp plus)
                {
                    var pl = new DiyFp((F << 1) + 1, E - 1).NormalizeBoundary();
                    var mi = F == HiddenBit ? new DiyFp((F << 2) - 1, E - 2) : new DiyFp((F << 1) - 1, E - 1);
                    minus = new DiyFp(mi.F << (mi.E - pl.E), pl.E);
                    plus = pl;
                }
            }

            // 10^(-348 + 8 i) as 64-bit significands rounded to nearest, the table of rapidjson (from double-conversion).
            private static (ulong F, int E)[] BuildCachedPowers()
            {
                var powers = new (ulong F, int E)[87];
                for (var index = 0; index < powers.Length; index++)
                {
                    var exponent10 = -348 + 8 * index;
                    System.Numerics.BigInteger numerator, denominator;
                    if (exponent10 >= 0)
                    {
                        numerator = System.Numerics.BigInteger.Pow(10, exponent10);
                        denominator = System.Numerics.BigInteger.One;
                    }
                    else
                    {
                        numerator = System.Numerics.BigInteger.One;
                        denominator = System.Numerics.BigInteger.Pow(10, -exponent10);
                    }
                    // Find e with 2^63 <= value / 2^e < 2^64, then round value / 2^e to the nearest integer.
                    var e = (int)Math.Floor(exponent10 * Math.Log(10, 2)) - 63;
                    System.Numerics.BigInteger f;
                    while (true)
                    {
                        var scaledNumerator = e >= 0 ? numerator : numerator << -e;
                        var scaledDenominator = e >= 0 ? denominator << e : denominator;
                        f = System.Numerics.BigInteger.DivRem(scaledNumerator, scaledDenominator, out var remainder);
                        if (remainder * 2 >= scaledDenominator)
                            f += 1;
                        if (f >= System.Numerics.BigInteger.One << 64)
                        {
                            e++;
                            continue;
                        }
                        if (f < System.Numerics.BigInteger.One << 63)
                        {
                            e--;
                            continue;
                        }
                        break;
                    }
                    powers[index] = ((ulong)f, e);
                }
                return powers;
            }

            private static DiyFp CachedPower(int e, out int k)
            {
                var dk = (-61 - e) * 0.30102999566398114 + 347;
                var ki = (int)dk;
                if (dk - ki > 0.0)
                    ki++;
                var index = (ki >> 3) + 1;
                k = -(-348 + (index << 3));
                return new DiyFp(CachedPowers[index].F, CachedPowers[index].E);
            }

            private static int CountDecimalDigit32(uint n)
            {
                if (n < 10) return 1;
                if (n < 100) return 2;
                if (n < 1000) return 3;
                if (n < 10000) return 4;
                if (n < 100000) return 5;
                if (n < 1000000) return 6;
                if (n < 10000000) return 7;
                if (n < 100000000) return 8;
                return 9;
            }

            private static void Round(char[] buffer, int length, ulong delta, ulong rest, ulong tenKappa, ulong wpw)
            {
                while (rest < wpw && delta - rest >= tenKappa && (rest + tenKappa < wpw || wpw - rest > rest + tenKappa - wpw))
                {
                    buffer[length - 1]--;
                    rest += tenKappa;
                }
            }

            private static void DigitGen(DiyFp w, DiyFp mp, ulong delta, char[] buffer, out int length, ref int k)
            {
                var one = new DiyFp(1UL << -mp.E, mp.E);
                var wpw = mp - w;
                var p1 = (uint)(mp.F >> -one.E);
                var p2 = mp.F & (one.F - 1);
                var kappa = CountDecimalDigit32(p1);
                length = 0;
                while (kappa > 0)
                {
                    var divisor = (uint)Pow10[kappa - 1];
                    var d = p1 / divisor;
                    p1 %= divisor;
                    if (d != 0 || length != 0)
                        buffer[length++] = (char)('0' + d);
                    kappa--;
                    var tmp = ((ulong)p1 << -one.E) + p2;
                    if (tmp <= delta)
                    {
                        k += kappa;
                        Round(buffer, length, delta, tmp, Pow10[kappa] << -one.E, wpw.F);
                        return;
                    }
                }
                while (true)
                {
                    p2 *= 10;
                    delta *= 10;
                    var d = (char)(p2 >> -one.E);
                    if (d != 0 || length != 0)
                        buffer[length++] = (char)('0' + d);
                    p2 &= one.F - 1;
                    kappa--;
                    if (p2 < delta)
                    {
                        k += kappa;
                        var index = -kappa;
                        // rapidjson 1.1.0, which Unity ships: no rounding from the ninth fractional digit on.
                        Round(buffer, length, delta, p2, one.F, wpw.F * (index < 9 ? Pow10[index] : 0));
                        return;
                    }
                }
            }

            public static void Digits(double value, char[] buffer, out int length, out int k)
            {
                var v = DiyFp.Of(value);
                v.NormalizedBoundaries(out var minus, out var plus);
                var cached = CachedPower(plus.E, out k);
                var w = v.Normalize() * cached;
                var wp = plus * cached;
                var wm = minus * cached;
                wm = new DiyFp(wm.F + 1, wm.E);
                wp = new DiyFp(wp.F - 1, wp.E);
                DigitGen(w, wp, wp.F - wm.F, buffer, out length, ref k);
            }

            public static string Prettify(char[] buffer, int length, int k)
            {
                var digits = new string(buffer, 0, length);
                var kk = length + k;
                if (k >= 0 && kk <= 21)
                    return digits + new string('0', kk - length) + ".0";
                if (kk > 0 && kk <= 21)
                    return digits.Substring(0, kk) + "." + digits.Substring(kk);
                if (kk > -6 && kk <= 0)
                    return "0." + new string('0', -kk) + digits;
                if (kk < -324)
                    return "0.0";
                var mantissa = length == 1 ? digits : digits.Substring(0, 1) + "." + digits.Substring(1);
                return mantissa + "e" + (kk - 1).ToString(CultureInfo.InvariantCulture);
            }
        }

        private static void WriteString(StringBuilder builder, string text)
        {
            builder.Append('"');
            foreach (var character in text)
            {
                switch (character)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    case '\b':
                        builder.Append("\\b");
                        break;
                    case '\f':
                        builder.Append("\\f");
                        break;
                    default:
                        if (character < 0x20)
                            builder.Append("\\u").Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
                        else
                            builder.Append(character);
                        break;
                }
            }
            builder.Append('"');
        }

        private static object ReadObject(JObject source, Type type)
        {
            var target = Activator.CreateInstance(type);
            foreach (var field in Fields(type))
            {
                if (!source.TryGetValue(field.Name, StringComparison.Ordinal, out var token))
                    continue;
                if (TryRead(token, field.FieldType, out var value))
                    field.SetValue(target, value);
            }
            return target;
        }

        private static bool TryRead(JToken token, Type type, out object value)
        {
            value = null;
            if (token.Type == JTokenType.Null)
            {
                if (type == typeof(string))
                {
                    value = string.Empty;
                    return true;
                }
                return false;
            }
            if (type == typeof(string))
            {
                if (token.Type is JTokenType.Object or JTokenType.Array)
                    return false;
                value = token.Type == JTokenType.Boolean ? ((bool)token ? "true" : "false") : (string)token;
                return true;
            }
            if (type == typeof(bool))
            {
                if (token.Type == JTokenType.Boolean)
                {
                    value = (bool)token;
                    return true;
                }
                return false;
            }
            if (type.IsEnum || type.IsPrimitive)
            {
                if (token.Type is not (JTokenType.Integer or JTokenType.Float))
                    return false;
                var underlying = type.IsEnum ? Enum.GetUnderlyingType(type) : type;
                object number;
                if (underlying == typeof(float))
                    number = (float)(double)token;
                else if (underlying == typeof(double))
                    number = (double)token;
                else if (underlying == typeof(char))
                    number = (char)(long)Math.Truncate((double)token);
                else
                    number = Convert.ChangeType(token.Type == JTokenType.Float ? Math.Truncate((double)token) : (object)(long)token, underlying, CultureInfo.InvariantCulture);
                value = type.IsEnum ? Enum.ToObject(type, number) : number;
                return true;
            }
            var element = ElementType(type);
            if (element != null)
            {
                if (token is not JArray array)
                    return false;
                var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element));
                foreach (var item in array)
                    list.Add(TryRead(item, element, out var itemValue) ? itemValue : DefaultOf(element));
                if (type.IsArray)
                {
                    var result = Array.CreateInstance(element, list.Count);
                    list.CopyTo(result, 0);
                    value = result;
                }
                else
                    value = list;
                return true;
            }
            if (token is not JObject nested)
                return false;
            value = ReadObject(nested, type);
            return true;
        }

        private static object DefaultOf(Type type) =>
            type == typeof(string) ? string.Empty : type.IsValueType || IsSerializableObject(type) ? Activator.CreateInstance(type) : null;
    }
}
