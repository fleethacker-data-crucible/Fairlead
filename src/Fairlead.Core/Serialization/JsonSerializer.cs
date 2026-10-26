using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace Fairlead.Core.Serialization
{
    /// <summary>How CLR objects map to JSON. Three configurations are used: <see cref="Api"/>, <see cref="File"/> and <see cref="Default"/>.</summary>
    public sealed class JsonSettings
    {
        /// <summary>Property names in camelCase (dictionary keys untouched).</summary>
        public bool CamelCaseNames { get; set; }
        /// <summary>Enums as camelCase names (flags as "a, b"; undefined values as numbers) instead of numbers.</summary>
        public bool EnumsAsStrings { get; set; }
        /// <summary>Null members are not written, and JSON nulls leave members as they are (Newtonsoft's NullValueHandling.Ignore). Dictionary values are always written.</summary>
        public bool IgnoreNulls { get; set; }
        /// <summary>Reading always creates new collections and objects instead of filling the ones a member already holds (Newtonsoft's ObjectCreationHandling.Replace).</summary>
        public bool ReplaceObjects { get; set; }

        /// <summary>The HTTP API: camelCase names, enums as camelCase strings, nulls written.</summary>
        public static readonly JsonSettings Api = new JsonSettings { CamelCaseNames = true, EnumsAsStrings = true };

        /// <summary>fairlead.json: camelCase names, enums as camelCase strings, nulls left out, collections replaced on read.</summary>
        public static readonly JsonSettings File = new JsonSettings { CamelCaseNames = true, EnumsAsStrings = true, IgnoreNulls = true, ReplaceObjects = true };

        /// <summary>Names as declared, enums as numbers, nulls written.</summary>
        public static readonly JsonSettings Default = new JsonSettings();
    }

    /// <summary>Text-level entry points with the names Fairlead used from Newtonsoft.Json.</summary>
    public static class JsonConvert
    {
        public static string SerializeObject(object value)
        {
            return SerializeObject(value, Formatting.None, JsonSettings.Default);
        }

        public static string SerializeObject(object value, Formatting formatting)
        {
            return SerializeObject(value, formatting, JsonSettings.Default);
        }

        public static string SerializeObject(object value, Formatting formatting, JsonSettings settings)
        {
            return JsonWriter.Write(JsonSerializer.ToToken(value, settings), formatting);
        }

        /// <summary>Null for the JSON literal null.</summary>
        public static T DeserializeObject<T>(string json)
        {
            return DeserializeObject<T>(json, JsonSettings.Default);
        }

        public static T DeserializeObject<T>(string json, JsonSettings settings)
        {
            var token = JsonReader.Parse(json);
            return (T)JsonSerializer.FromToken(token, typeof(T), settings, null);
        }
    }

    /// <summary>
    /// Reflection mapping between CLR objects and tokens, following Newtonsoft.Json's default contract: public
    /// fields then public readable properties (no indexers, no statics), dictionaries as objects, other
    /// sequences as arrays, ISO dates. Reading matches names exactly, then ignoring case, and fills existing
    /// collections and objects the way Newtonsoft's default (ObjectCreationHandling.Auto) did.
    /// </summary>
    public static class JsonSerializer
    {
        private sealed class Member
        {
            public string Name;
            public Type Type;
            public PropertyInfo Property;
            public FieldInfo Field;
            public bool CanRead;
            public bool CanWrite;

            public object Get(object target)
            {
                return Property != null ? Property.GetValue(target, null) : Field.GetValue(target);
            }

            public void Set(object target, object value)
            {
                if (Property != null) Property.SetValue(target, value, null);
                else Field.SetValue(target, value);
            }
        }

        private static readonly Dictionary<Type, List<Member>> MemberCache = new Dictionary<Type, List<Member>>();
        private static readonly object CacheLock = new object();

        private static List<Member> MembersOf(Type type)
        {
            lock (CacheLock)
            {
                List<Member> list;
                if (MemberCache.TryGetValue(type, out list)) return list;
                list = new List<Member>();
                foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                    list.Add(new Member { Name = f.Name, Type = f.FieldType, Field = f, CanRead = true, CanWrite = !f.IsInitOnly });
                foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (p.GetIndexParameters().Length > 0) continue;
                    if (list.Any(m => m.Name == p.Name)) continue;
                    var getter = p.GetGetMethod(false);
                    var setter = p.GetSetMethod(false);
                    if (getter == null && setter == null) continue;
                    list.Add(new Member { Name = p.Name, Type = p.PropertyType, Property = p, CanRead = getter != null, CanWrite = setter != null });
                }
                MemberCache[type] = list;
                return list;
            }
        }

        // ---- names ----

        /// <summary>Newtonsoft's camelCase rule: lower the leading run of capitals, keeping the last one of a run that starts a word.</summary>
        public static string ToCamelCase(string s)
        {
            if (string.IsNullOrEmpty(s) || !char.IsUpper(s[0])) return s;
            var chars = s.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (i == 1 && !char.IsUpper(chars[i])) break;
                var hasNext = i + 1 < chars.Length;
                if (i > 0 && hasNext && !char.IsUpper(chars[i + 1]))
                {
                    if (char.IsSeparator(chars[i + 1])) chars[i] = char.ToLower(chars[i], CultureInfo.InvariantCulture);
                    break;
                }
                chars[i] = char.ToLower(chars[i], CultureInfo.InvariantCulture);
            }
            return new string(chars);
        }

        private static JToken EnumToken(object value, JsonSettings settings)
        {
            if (!settings.EnumsAsStrings) return JValue.Integer(EnumNumber(value));
            var text = value.ToString();
            if (text.Length > 0 && (char.IsDigit(text[0]) || text[0] == '-')) return JValue.Integer(EnumNumber(value));
            return new JValue(string.Join(", ", text.Split(new[] { ", " }, StringSplitOptions.None).Select(ToCamelCase).ToArray()));
        }

        private static object EnumNumber(object value)
        {
            var underlying = Enum.GetUnderlyingType(value.GetType());
            if (underlying == typeof(ulong)) return Convert.ToUInt64(value, CultureInfo.InvariantCulture);
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        // ---- CLR -> token ----

        public static JToken ToToken(object value, JsonSettings settings)
        {
            return ToToken(value, settings, 0);
        }

        private static JToken ToToken(object value, JsonSettings settings, int depth)
        {
            if (depth > 64) throw new JsonException("Self referencing loop or nesting too deep while serializing " + value.GetType().Name + ".");
            if (value == null) return JValue.CreateNull();
            var token = value as JToken;
            if (token != null) return token;
            var type = value.GetType();
            if (value is string) return new JValue((string)value);
            if (value is char) return new JValue(value.ToString());
            if (value is bool) return new JValue((bool)value);
            if (type.IsEnum) return EnumToken(value, settings);
            if (value is int || value is long || value is short || value is byte || value is sbyte || value is ushort || value is uint) return new JValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
            if (value is ulong) return JValue.Integer(value);
            if (value is double || value is float || value is decimal) return new JValue(value);
            if (value is DateTime) return new JValue(JsonWriter.FormatDate((DateTime)value));
            if (value is DateTimeOffset) return new JValue(JsonWriter.FormatDate((DateTimeOffset)value));
            if (value is TimeSpan) return new JValue(((TimeSpan)value).ToString());
            if (value is Guid) return new JValue(((Guid)value).ToString("D"));
            if (value is Uri) return new JValue(((Uri)value).OriginalString);
            if (value is byte[]) return new JValue(Convert.ToBase64String((byte[])value));
            if (value is Type) return new JValue(((Type)value).AssemblyQualifiedName);

            if (IsDictionary(type))
            {
                var o = new JObject();
                foreach (var entry in (IEnumerable)value)
                {
                    object key;
                    object item;
                    if (entry is DictionaryEntry) { key = ((DictionaryEntry)entry).Key; item = ((DictionaryEntry)entry).Value; }
                    else
                    {
                        var et = entry.GetType();
                        key = et.GetProperty("Key").GetValue(entry, null);
                        item = et.GetProperty("Value").GetValue(entry, null);
                    }
                    o[KeyText(key)] = ToToken(item, settings, depth + 1);
                }
                return o;
            }
            var sequence = value as IEnumerable;
            if (sequence != null)
            {
                var a = new JArray();
                foreach (var item in sequence) a.Add(ToToken(item, settings, depth + 1));
                return a;
            }

            var result = new JObject();
            foreach (var m in MembersOf(type))
            {
                if (!m.CanRead) continue;
                var name = settings.CamelCaseNames ? ToCamelCase(m.Name) : m.Name;
                if (result.ContainsKey(name)) throw new JsonException("A member with the name '" + name + "' already exists on '" + type.Name + "'.");
                var memberValue = m.Get(value);
                if (memberValue == null && settings.IgnoreNulls) continue;
                result.Add(name, ToToken(memberValue, settings, depth + 1));
            }
            return result;
        }

        private static string KeyText(object key)
        {
            if (key == null) return string.Empty;
            if (key is string) return (string)key;
            if (key is DateTime) return JsonWriter.FormatDate((DateTime)key);
            if (key is DateTimeOffset) return JsonWriter.FormatDate((DateTimeOffset)key);
            return Convert.ToString(key, CultureInfo.InvariantCulture);
        }

        private static bool IsDictionary(Type type)
        {
            if (typeof(IDictionary).IsAssignableFrom(type)) return true;
            return GenericInterface(type, typeof(IDictionary<,>)) != null || GenericInterface(type, typeof(IReadOnlyDictionary<,>)) != null;
        }

        private static Type GenericInterface(Type type, Type definition)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == definition) return type;
            foreach (var i in type.GetInterfaces())
                if (i.IsGenericType && i.GetGenericTypeDefinition() == definition) return i;
            return null;
        }

        // ---- token -> CLR ----

        private static JsonException Mismatch(JToken token, Type type)
        {
            return new JsonException("Cannot convert JSON " + (token == null ? "null" : token.Type.ToString().ToLowerInvariant()) + " to " + type.Name + ".");
        }

        /// <summary>Reads <paramref name="token"/> as <paramref name="type"/>; <paramref name="existing"/> is filled instead of replaced where Newtonsoft did that.</summary>
        public static object FromToken(JToken token, Type type, JsonSettings settings, object existing)
        {
            if (type == typeof(object))
            {
                if (token == null || token.Type == JTokenType.Null) return null;
                var jv = token as JValue;
                return jv != null ? jv.Value : token;
            }
            if (typeof(JToken).IsAssignableFrom(type))
            {
                if (token == null) return null;
                if (!type.IsInstanceOfType(token))
                {
                    if (token.Type == JTokenType.Null) return null;
                    throw Mismatch(token, type);
                }
                return token;
            }
            var nullable = Nullable.GetUnderlyingType(type);
            if (token == null || token.Type == JTokenType.Null)
            {
                if (!type.IsValueType || nullable != null) return null;
                throw new JsonException("Error converting value {null} to type '" + type.FullName + "'.");
            }
            if (nullable != null) type = nullable;

            if (type == typeof(string))
            {
                if (!(token is JValue)) throw Mismatch(token, type);
                return (string)token;
            }
            if (type.IsEnum) return ReadEnum(token, type);
            if (type == typeof(DateTimeOffset))
            {
                var v = Scalar(token, type);
                if (v is string) return DateTimeOffset.Parse((string)v, CultureInfo.InvariantCulture);
                throw Mismatch(token, type);
            }
            if (type == typeof(DateTime))
            {
                var v = Scalar(token, type);
                if (v is string) return DateTime.Parse((string)v, CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
                throw Mismatch(token, type);
            }
            if (type == typeof(Guid)) return new Guid(Convert.ToString(Scalar(token, type), CultureInfo.InvariantCulture));
            if (type == typeof(TimeSpan)) return TimeSpan.Parse(Convert.ToString(Scalar(token, type), CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            if (type == typeof(Uri)) return new Uri(Convert.ToString(Scalar(token, type), CultureInfo.InvariantCulture), UriKind.RelativeOrAbsolute);
            if (type == typeof(byte[]))
            {
                var a = token as JArray;
                if (a != null) return a.Select(x => (byte)(int)x).ToArray();
                return Convert.FromBase64String(Convert.ToString(Scalar(token, type), CultureInfo.InvariantCulture));
            }
            if (type.IsPrimitive || type == typeof(decimal))
                return Convert.ChangeType(Scalar(token, type), type, CultureInfo.InvariantCulture);

            if (IsDictionary(type)) return ReadDictionary(token, type, settings, existing);
            if (type.IsArray)
            {
                var element = type.GetElementType();
                var items = ReadItems(token, type, element, settings);
                var array = Array.CreateInstance(element, items.Count);
                for (var i = 0; i < items.Count; i++) array.SetValue(items[i], i);
                return array;
            }
            var enumerable = GenericInterface(type, typeof(IEnumerable<>));
            if (enumerable != null) return ReadCollection(token, type, enumerable.GetGenericArguments()[0], settings, existing);

            var obj = token as JObject;
            if (obj == null) throw Mismatch(token, type);
            var target = existing ?? CreateInstance(type);
            var members = MembersOf(type);
            foreach (var p in obj.Properties())
            {
                var m = members.FirstOrDefault(x => x.Name == p.Name) ?? members.FirstOrDefault(x => string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase));
                if (m == null) continue;
                if (settings.IgnoreNulls && p.Value.Type == JTokenType.Null) continue;
                var current = !settings.ReplaceObjects && m.CanRead && !m.Type.IsValueType && m.Type != typeof(string) && !m.Type.IsArray && p.Value.Type != JTokenType.Null ? m.Get(target) : null;
                var reuse = current != null && !(current is JToken) && (p.Value.Type == JTokenType.Object || p.Value.Type == JTokenType.Array);
                if (!m.CanWrite)
                {
                    // A read-only member is filled in place when it already holds a collection or object, else skipped.
                    if (reuse) FromToken(p.Value, m.Type, settings, current);
                    continue;
                }
                var value = FromToken(p.Value, m.Type, settings, reuse ? current : null);
                m.Set(target, value);
            }
            return target;
        }

        private static object CreateInstance(Type type)
        {
            if (type.IsInterface || type.IsAbstract) throw new JsonException("Could not create an instance of type " + type.FullName + ". Type is an interface or abstract class.");
            var ctor = type.GetConstructor(Type.EmptyTypes);
            if (ctor == null && !type.IsValueType) throw new JsonException("Unable to find a constructor to use for type " + type.FullName + ".");
            return Activator.CreateInstance(type);
        }

        private static object Scalar(JToken token, Type type)
        {
            var v = token as JValue;
            if (v == null) throw Mismatch(token, type);
            return v.Value;
        }

        private static object ReadEnum(JToken token, Type type)
        {
            var v = Scalar(token, type);
            var s = v as string;
            if (s != null)
            {
                s = s.Trim();
                foreach (var name in Enum.GetNames(type))
                    if (string.Equals(name, s, StringComparison.OrdinalIgnoreCase)) return Enum.Parse(type, name);
                long n;
                if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return Enum.ToObject(type, n);
                try { return Enum.Parse(type, s, true); }
                catch (ArgumentException) { throw new JsonException("Requested value '" + s + "' was not found in " + type.Name + "."); }
            }
            if (v is long || v is ulong || v is decimal || v is double) return Enum.ToObject(type, Convert.ToInt64(v, CultureInfo.InvariantCulture));
            throw Mismatch(token, type);
        }

        private static List<object> ReadItems(JToken token, Type type, Type element, JsonSettings settings)
        {
            var a = token as JArray;
            if (a == null) throw Mismatch(token, type);
            var list = new List<object>();
            foreach (var item in a) list.Add(FromToken(item, element, settings, null));
            return list;
        }

        private static object ReadCollection(JToken token, Type type, Type element, JsonSettings settings, object existing)
        {
            var items = ReadItems(token, type, element, settings);
            var target = existing;
            if (target == null)
            {
                if (type.IsInterface || type.IsAbstract)
                {
                    var listType = typeof(List<>).MakeGenericType(element);
                    if (!type.IsAssignableFrom(listType)) throw new JsonException("Could not create an instance of type " + type.FullName + ".");
                    target = Activator.CreateInstance(listType);
                }
                else target = CreateInstance(type);
            }
            var list = target as IList;
            if (list != null && !list.IsReadOnly && !list.IsFixedSize)
            {
                foreach (var i in items) list.Add(i);
                return target;
            }
            var add = target.GetType().GetMethod("Add", new[] { element });
            if (add == null) throw new JsonException("Cannot add items to " + target.GetType().FullName + ".");
            foreach (var i in items) add.Invoke(target, new[] { i });
            return target;
        }

        private static object ReadDictionary(JToken token, Type type, JsonSettings settings, object existing)
        {
            var o = token as JObject;
            if (o == null) throw Mismatch(token, type);
            var iface = GenericInterface(type, typeof(IDictionary<,>)) ?? GenericInterface(type, typeof(IReadOnlyDictionary<,>));
            var keyType = iface == null ? typeof(string) : iface.GetGenericArguments()[0];
            var valueType = iface == null ? typeof(object) : iface.GetGenericArguments()[1];
            var target = existing;
            if (target == null)
            {
                if (type.IsInterface || type.IsAbstract) target = Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(keyType, valueType));
                else target = CreateInstance(type);
            }
            var dict = target as IDictionary;
            foreach (var p in o.Properties())
            {
                object key = p.Name;
                if (keyType.IsEnum) key = ReadEnum(new JValue(p.Name), keyType);
                else if (keyType != typeof(string) && keyType != typeof(object)) key = Convert.ChangeType(p.Name, Nullable.GetUnderlyingType(keyType) ?? keyType, CultureInfo.InvariantCulture);
                var value = FromToken(p.Value, valueType, settings, null);
                if (dict != null) dict[key] = value;
                else
                {
                    var indexer = target.GetType().GetProperty("Item", new[] { keyType });
                    if (indexer == null) throw new JsonException("Cannot set entries of " + target.GetType().FullName + ".");
                    indexer.SetValue(target, value, new[] { key });
                }
            }
            return target;
        }
    }
}
