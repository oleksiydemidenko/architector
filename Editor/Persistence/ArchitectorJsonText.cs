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
            if (field.IsInitOnly || field.IsLiteral || field.IsNotSerialized)
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

        public static string FormatReal(double value)
        {
            if (double.IsNaN(value))
                return "NaN";
            if (double.IsPositiveInfinity(value))
                return "Infinity";
            if (double.IsNegativeInfinity(value))
                return "-Infinity";
            var text = value.ToString("R", CultureInfo.InvariantCulture);
            if (text.IndexOf('E') >= 0)
            {
                var exponent = text.IndexOf('E');
                var mantissa = text.Substring(0, exponent);
                var power = int.Parse(text.Substring(exponent + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                return mantissa + "e" + (power < 0 ? "-" : "+") + Math.Abs(power).ToString("00", CultureInfo.InvariantCulture);
            }
            if (text.IndexOf('.') < 0)
                text += ".0";
            if (text == "0.0" && BitConverter.DoubleToInt64Bits(value) < 0)
                text = "-0.0";
            return text;
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
