// Built-in JSON for Fairlead (replaces Newtonsoft.Json, so Fairlead ships no third-party DLL). C# 5 only.
// It covers the part of the Newtonsoft API Fairlead uses: the JToken tree (JObject, JArray, JValue) for request
// translation, and JsonConvert for the config file and the admin API (camelCase names, enums as camelCase strings).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace Fairlead.Core.JsonLite
{
    public enum JTokenType { None, Object, Array, Integer, Float, String, Boolean, Null }

    public enum Formatting { None, Indented }

    public enum NullValueHandling { Include, Ignore }

    public sealed class JsonException : Exception
    {
        public JsonException(string message) : base(message) { }
    }

    public abstract class JToken
    {
        public abstract JTokenType Type { get; }
        public JToken Parent { get; internal set; }

        // Newtonsoft returns null for a missing key or a non-object; so does this.
        public virtual JToken this[object key]
        {
            get { return null; }
            set { throw new InvalidOperationException("Cannot set a child value on " + Type + "."); }
        }

        public T Value<T>(object key)
        {
            JToken t = this[key];
            return t == null ? default(T) : t.ToObject<T>();
        }

        public T ToObject<T>() { return (T)JsonConvert.FromToken(this, typeof(T), JsonSerializerSettings.Default); }
        public T ToObject<T>(JsonSerializer serializer) { return (T)JsonConvert.FromToken(this, typeof(T), serializer.Settings); }

        public abstract JToken DeepClone();

        public override string ToString() { return ToString(Formatting.Indented); }

        public string ToString(Formatting formatting)
        {
            StringBuilder sb = new StringBuilder();
            JsonWriter.Write(sb, this, formatting == Formatting.Indented, 0);
            return sb.ToString();
        }

        public static JToken Parse(string json) { return JsonReader.Parse(json); }

        public static JToken FromObject(object o) { return JsonConvert.ToToken(o, JsonSerializerSettings.Default); }

        public static implicit operator JToken(string v) { return v == null ? JValue.CreateNull() : new JValue(v); }
        public static implicit operator JToken(bool v) { return new JValue(v); }
        public static implicit operator JToken(int v) { return new JValue((long)v); }
        public static implicit operator JToken(long v) { return new JValue(v); }
        public static implicit operator JToken(double v) { return new JValue(v); }
        public static implicit operator JToken(int? v) { return v.HasValue ? new JValue((long)v.Value) : JValue.CreateNull(); }
        public static implicit operator JToken(long? v) { return v.HasValue ? new JValue(v.Value) : JValue.CreateNull(); }
        public static implicit operator JToken(double? v) { return v.HasValue ? new JValue(v.Value) : JValue.CreateNull(); }
        public static implicit operator JToken(bool? v) { return v.HasValue ? new JValue(v.Value) : JValue.CreateNull(); }

        public static explicit operator string(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            JValue v = t as JValue;
            if (v == null) throw new InvalidCastException("Cannot convert " + t.Type + " to String.");
            return v.Value is string ? (string)v.Value : JsonWriter.Scalar(v);
        }
        public static explicit operator bool(JToken t) { return Convert.ToBoolean(Raw(t, "Boolean"), CultureInfo.InvariantCulture); }
        public static explicit operator bool?(JToken t) { return Null(t) ? (bool?)null : (bool)t; }
        public static explicit operator int(JToken t) { return Convert.ToInt32(Raw(t, "Int32"), CultureInfo.InvariantCulture); }
        public static explicit operator int?(JToken t) { return Null(t) ? (int?)null : (int)t; }
        public static explicit operator long(JToken t) { return Convert.ToInt64(Raw(t, "Int64"), CultureInfo.InvariantCulture); }
        public static explicit operator long?(JToken t) { return Null(t) ? (long?)null : (long)t; }
        public static explicit operator double(JToken t) { return Convert.ToDouble(Raw(t, "Double"), CultureInfo.InvariantCulture); }
        public static explicit operator double?(JToken t) { return Null(t) ? (double?)null : (double)t; }

        static bool Null(JToken t) { return t == null || t.Type == JTokenType.Null; }

        static object Raw(JToken t, string target)
        {
            JValue v = t as JValue;
            if (v == null || v.Value == null) throw new InvalidCastException("Cannot convert " + (t == null ? "null" : t.Type.ToString()) + " to " + target + ".");
            return v.Value;
        }
    }

    public sealed class JValue : JToken
    {
        readonly JTokenType _type;
        public object Value { get; private set; }

        public JValue(string v) { Value = v; _type = v == null ? JTokenType.Null : JTokenType.String; }
        public JValue(bool v) { Value = v; _type = JTokenType.Boolean; }
        public JValue(long v) { Value = v; _type = JTokenType.Integer; }
        public JValue(int v) { Value = (long)v; _type = JTokenType.Integer; }
        public JValue(double v) { Value = v; _type = JTokenType.Float; }
        JValue(object v, JTokenType t) { Value = v; _type = t; }

        public static JValue CreateNull() { return new JValue(null, JTokenType.Null); }
        internal static JValue Number(object v, bool integer) { return new JValue(v, integer ? JTokenType.Integer : JTokenType.Float); }

        public override JTokenType Type { get { return _type; } }
        public override JToken DeepClone() { return new JValue(Value, _type); }
    }

    public sealed class JProperty
    {
        public string Name { get; private set; }
        public JToken Value { get; private set; }
        public JProperty(string name, JToken value) { Name = name; Value = value; }
    }

    public sealed class JObject : JToken, IEnumerable<KeyValuePair<string, JToken>>
    {
        readonly List<string> _order = new List<string>();
        readonly Dictionary<string, JToken> _map = new Dictionary<string, JToken>(StringComparer.Ordinal);

        public JObject() { }

        public override JTokenType Type { get { return JTokenType.Object; } }
        public int Count { get { return _order.Count; } }

        public override JToken this[object key]
        {
            get
            {
                JToken t;
                return key is string && _map.TryGetValue((string)key, out t) ? t : null;
            }
            set { this[(string)key] = value; }
        }

        public JToken this[string key]
        {
            get { JToken t; return key != null && _map.TryGetValue(key, out t) ? t : null; }
            set
            {
                if (value == null) value = JValue.CreateNull();
                if (value.Parent != null) value = value.DeepClone();
                if (!_map.ContainsKey(key)) _order.Add(key);
                _map[key] = value;
                value.Parent = this;
            }
        }

        public void Add(string key, JToken value)
        {
            if (_map.ContainsKey(key)) throw new ArgumentException("Property '" + key + "' already exists.");
            this[key] = value;
        }

        public void Add(JProperty p) { Add(p.Name, p.Value); }

        public bool ContainsKey(string key) { return _map.ContainsKey(key); }
        public bool TryGetValue(string key, out JToken value) { return _map.TryGetValue(key, out value); }

        public bool Remove(string key)
        {
            JToken t;
            if (!_map.TryGetValue(key, out t)) return false;
            _map.Remove(key);
            _order.Remove(key);
            t.Parent = null;
            return true;
        }

        public IEnumerable<JProperty> Properties()
        {
            List<JProperty> list = new List<JProperty>(_order.Count);
            foreach (string k in _order) list.Add(new JProperty(k, _map[k]));
            return list;
        }

        public JProperty Property(string name)
        {
            JToken t;
            return _map.TryGetValue(name, out t) ? new JProperty(name, t) : null;
        }

        public override JToken DeepClone()
        {
            JObject o = new JObject();
            foreach (string k in _order) o[k] = _map[k].DeepClone();
            return o;
        }

        public IEnumerator<KeyValuePair<string, JToken>> GetEnumerator()
        {
            List<KeyValuePair<string, JToken>> list = new List<KeyValuePair<string, JToken>>(_order.Count);
            foreach (string k in _order) list.Add(new KeyValuePair<string, JToken>(k, _map[k]));
            return list.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() { return GetEnumerator(); }

        public static new JObject Parse(string json)
        {
            JObject o = JsonReader.Parse(json) as JObject;
            if (o == null) throw new JsonException("Expected a JSON object.");
            return o;
        }

        public static new JObject FromObject(object value)
        {
            JObject o = JsonConvert.ToToken(value, JsonSerializerSettings.Default) as JObject;
            if (o == null) throw new ArgumentException("Value does not serialize to a JSON object.");
            return o;
        }
    }

    public sealed class JArray : JToken, IList<JToken>
    {
        readonly List<JToken> _items = new List<JToken>();

        public JArray() { }
        public JArray(params object[] items) { foreach (object o in items) Add(o as JToken ?? JsonConvert.ToToken(o, JsonSerializerSettings.Default)); }

        public override JTokenType Type { get { return JTokenType.Array; } }
        public int Count { get { return _items.Count; } }
        public bool IsReadOnly { get { return false; } }

        public override JToken this[object key]
        {
            get { return key is int && (int)key >= 0 && (int)key < _items.Count ? _items[(int)key] : null; }
            set { this[(int)key] = value; }
        }

        public JToken this[int index]
        {
            get { return _items[index]; }
            set { _items[index] = Own(value); }
        }

        JToken Own(JToken t)
        {
            if (t == null) t = JValue.CreateNull();
            if (t.Parent != null) t = t.DeepClone();
            t.Parent = this;
            return t;
        }

        public void Add(JToken item) { _items.Add(Own(item)); }
        public void AddFirst(JToken item) { _items.Insert(0, Own(item)); }
        public void Insert(int index, JToken item) { _items.Insert(index, Own(item)); }
        public void RemoveAt(int index) { _items.RemoveAt(index); }
        public bool Remove(JToken item) { return _items.Remove(item); }
        public void Clear() { _items.Clear(); }
        public bool Contains(JToken item) { return _items.Contains(item); }
        public int IndexOf(JToken item) { return _items.IndexOf(item); }
        public void CopyTo(JToken[] array, int index) { _items.CopyTo(array, index); }

        public override JToken DeepClone()
        {
            JArray a = new JArray();
            foreach (JToken t in _items) a.Add(t.DeepClone());
            return a;
        }

        public IEnumerator<JToken> GetEnumerator() { return _items.GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() { return GetEnumerator(); }

        public static new JArray Parse(string json)
        {
            JArray a = JsonReader.Parse(json) as JArray;
            if (a == null) throw new JsonException("Expected a JSON array.");
            return a;
        }
    }

    // Null-safe navigation for C# 5 (stands in for the ?[ ] operator): token.Get("a").Get(0) is null when any step is missing.
    public static class JTokenExtensions
    {
        public static JToken Get(this JToken t, object key) { return t == null ? null : t[key]; }

        // The first child of an array (or null), like Newtonsoft's token.FirstOrDefault() on a JArray.
        public static JToken FirstChild(this JToken t)
        {
            JArray a = t as JArray;
            return a != null && a.Count > 0 ? a[0] : null;
        }
    }

    // ---- settings and the POCO serializer ----

    public sealed class JsonSerializerSettings
    {
        public NullValueHandling NullValueHandling = NullValueHandling.Include;
        public Formatting Formatting = Formatting.None;
        public bool CamelCaseProperties = true;     // property names camelCase; dictionary keys left as they are
        public bool EnumsAsCamelCaseStrings = true;

        internal static readonly JsonSerializerSettings Default = new JsonSerializerSettings { CamelCaseProperties = false, EnumsAsCamelCaseStrings = false };
    }

    public sealed class JsonSerializer
    {
        public JsonSerializerSettings Settings { get; private set; }
        public static JsonSerializer Create(JsonSerializerSettings settings) { return new JsonSerializer { Settings = settings }; }
    }

    public static class JsonConvert
    {
        public static string SerializeObject(object value) { return SerializeObject(value, JsonSerializerSettings.Default); }

        public static string SerializeObject(object value, JsonSerializerSettings s)
        {
            return ToToken(value, s).ToString(s.Formatting);
        }

        public static T DeserializeObject<T>(string json) { return DeserializeObject<T>(json, JsonSerializerSettings.Default); }

        public static T DeserializeObject<T>(string json, JsonSerializerSettings s)
        {
            return (T)FromToken(JsonReader.Parse(json), typeof(T), s);
        }

        // Newtonsoft's CamelCaseNamingStrategy: lower-case the leading run of capitals ("URLPath" -> "urlPath").
        public static string CamelCase(string s)
        {
            if (string.IsNullOrEmpty(s) || !char.IsUpper(s[0])) return s;
            char[] c = s.ToCharArray();
            for (int i = 0; i < c.Length; i++)
            {
                if (i == 1 && !char.IsUpper(c[i])) break;
                bool hasNext = i + 1 < c.Length;
                if (i > 0 && hasNext && !char.IsUpper(c[i + 1]))
                {
                    if (char.IsSeparator(c[i + 1])) c[i] = char.ToLowerInvariant(c[i]);
                    break;
                }
                c[i] = char.ToLowerInvariant(c[i]);
            }
            return new string(c);
        }

        static string EnumName(object e, JsonSerializerSettings s)
        {
            string n = e.ToString();
            return s.EnumsAsCamelCaseStrings ? CamelCase(n) : n;
        }

        internal static JToken ToToken(object v, JsonSerializerSettings s)
        {
            if (v == null) return JValue.CreateNull();
            JToken tok = v as JToken;
            if (tok != null) return tok.DeepClone();
            Type t = v.GetType();
            if (v is string) return new JValue((string)v);
            if (v is bool) return new JValue((bool)v);
            if (t.IsEnum) return s.EnumsAsCamelCaseStrings ? new JValue(EnumName(v, s)) : new JValue(Convert.ToInt64(v, CultureInfo.InvariantCulture));
            if (v is int || v is long || v is short || v is byte || v is uint || v is sbyte || v is ushort) return new JValue(Convert.ToInt64(v, CultureInfo.InvariantCulture));
            if (v is ulong) return JValue.Number(v, true);
            if (v is double || v is float) return new JValue(Convert.ToDouble(v, CultureInfo.InvariantCulture));
            if (v is decimal) return JValue.Number(v, false);
            if (v is char) return new JValue(v.ToString());
            if (v is DateTime) return new JValue(((DateTime)v).ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", CultureInfo.InvariantCulture));
            if (v is DateTimeOffset) return new JValue(((DateTimeOffset)v).ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture));
            if (v is TimeSpan) return new JValue(((TimeSpan)v).ToString("c", CultureInfo.InvariantCulture));
            if (v is Guid || v is Uri) return new JValue(v.ToString());

            IDictionary dict = v as IDictionary;
            if (dict != null)
            {
                JObject o = new JObject();
                foreach (DictionaryEntry e in dict)
                {
                    if (e.Value == null && s.NullValueHandling == NullValueHandling.Ignore) continue;
                    o[Convert.ToString(e.Key, CultureInfo.InvariantCulture)] = ToToken(e.Value, s);
                }
                return o;
            }
            IEnumerable seq = v as IEnumerable;
            if (seq != null)
            {
                JArray a = new JArray();
                foreach (object item in seq) a.Add(ToToken(item, s));
                return a;
            }

            JObject obj = new JObject();
            foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || p.GetIndexParameters().Length > 0 || p.GetGetMethod() == null) continue;
                object pv = p.GetValue(v, null);
                if (pv == null && s.NullValueHandling == NullValueHandling.Ignore) continue;
                obj[s.CamelCaseProperties ? CamelCase(p.Name) : p.Name] = ToToken(pv, s);
            }
            foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                object fv = f.GetValue(v);
                if (fv == null && s.NullValueHandling == NullValueHandling.Ignore) continue;
                obj[s.CamelCaseProperties ? CamelCase(f.Name) : f.Name] = ToToken(fv, s);
            }
            return obj;
        }

        internal static object FromToken(JToken tok, Type t, JsonSerializerSettings s)
        {
            Type under = Nullable.GetUnderlyingType(t);
            if (tok == null || tok.Type == JTokenType.Null)
            {
                if (under != null || !t.IsValueType) return null;
                return Activator.CreateInstance(t);
            }
            if (under != null) t = under;
            if (typeof(JToken).IsAssignableFrom(t))
            {
                if (!t.IsInstanceOfType(tok)) throw new JsonException("Expected " + t.Name + ", found " + tok.Type + ".");
                return tok.DeepClone();
            }
            JValue val = tok as JValue;
            if (t == typeof(object)) return val != null ? val.Value : (object)tok.DeepClone();
            if (t == typeof(string))
            {
                if (val == null) throw new JsonException("Expected a string, found " + tok.Type + ".");
                return (string)tok;
            }
            if (t.IsEnum)
            {
                if (val != null && val.Value is string)
                {
                    foreach (string name in Enum.GetNames(t))
                        if (string.Equals(name, (string)val.Value, StringComparison.OrdinalIgnoreCase)) return Enum.Parse(t, name);
                    throw new JsonException("'" + val.Value + "' is not a valid " + t.Name + ".");
                }
                if (val != null && tok.Type == JTokenType.Integer) return Enum.ToObject(t, Convert.ToInt64(val.Value));
                throw new JsonException("Expected a " + t.Name + " name.");
            }
            if (t.IsPrimitive || t == typeof(decimal))
            {
                if (val == null) throw new JsonException("Expected a " + t.Name + ", found " + tok.Type + ".");
                try { return Convert.ChangeType(val.Value, t, CultureInfo.InvariantCulture); }
                catch (Exception e) { throw new JsonException("Cannot convert '" + val.Value + "' to " + t.Name + ": " + e.Message); }
            }
            if (t == typeof(DateTime)) return DateTime.Parse((string)tok, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            if (t == typeof(DateTimeOffset)) return DateTimeOffset.Parse((string)tok, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            if (t == typeof(TimeSpan)) return TimeSpan.Parse((string)tok, CultureInfo.InvariantCulture);
            if (t == typeof(Guid)) return new Guid((string)tok);
            if (t == typeof(Uri)) return new Uri((string)tok, UriKind.RelativeOrAbsolute);

            if (t.IsArray)
            {
                JArray arr = Expect<JArray>(tok, t);
                Type et = t.GetElementType();
                Array result = Array.CreateInstance(et, arr.Count);
                for (int i = 0; i < arr.Count; i++) result.SetValue(FromToken(arr[i], et, s), i);
                return result;
            }
            Type dictType = GenericInterface(t, typeof(IDictionary<,>));
            if (dictType != null)
            {
                JObject o = Expect<JObject>(tok, t);
                Type vt = dictType.GetGenericArguments()[1];
                IDictionary d = (IDictionary)Activator.CreateInstance(ConcreteType(t, typeof(Dictionary<,>), dictType.GetGenericArguments()));
                foreach (KeyValuePair<string, JToken> kv in o) d[kv.Key] = FromToken(kv.Value, vt, s);
                return d;
            }
            Type listType = GenericInterface(t, typeof(IEnumerable<>));
            if (listType != null && t != typeof(string))
            {
                JArray arr = Expect<JArray>(tok, t);
                Type et = listType.GetGenericArguments()[0];
                IList list = (IList)Activator.CreateInstance(ConcreteType(t, typeof(List<>), new Type[] { et }));
                foreach (JToken item in arr) list.Add(FromToken(item, et, s));
                return list;
            }

            JObject src = Expect<JObject>(tok, t);
            object target = Activator.CreateInstance(t, true);
            Dictionary<string, PropertyInfo> props = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                if (p.GetIndexParameters().Length == 0 && !props.ContainsKey(p.Name)) props[p.Name] = p;
            Dictionary<string, FieldInfo> fields = new Dictionary<string, FieldInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.Instance)) fields[f.Name] = f;
            foreach (KeyValuePair<string, JToken> kv in src)
            {
                PropertyInfo p;
                FieldInfo f;
                if (props.TryGetValue(kv.Key, out p))
                {
                    MethodInfo setter = p.GetSetMethod();
                    if (setter != null) { p.SetValue(target, FromToken(kv.Value, p.PropertyType, s), null); continue; }
                    // A get-only collection is filled in place.
                    object existing = p.CanRead ? p.GetValue(target, null) : null;
                    IList el = existing as IList;
                    IDictionary ed = existing as IDictionary;
                    if (el != null && kv.Value is JArray)
                    {
                        el.Clear();
                        IList items = (IList)FromToken(kv.Value, p.PropertyType, s);
                        foreach (object item in items) el.Add(item);
                    }
                    else if (ed != null && kv.Value is JObject)
                    {
                        ed.Clear();
                        IDictionary items = (IDictionary)FromToken(kv.Value, p.PropertyType, s);
                        foreach (DictionaryEntry e in items) ed[e.Key] = e.Value;
                    }
                }
                else if (fields.TryGetValue(kv.Key, out f) && !f.IsInitOnly) f.SetValue(target, FromToken(kv.Value, f.FieldType, s));
            }
            return target;
        }

        static T Expect<T>(JToken tok, Type target) where T : JToken
        {
            T x = tok as T;
            if (x == null) throw new JsonException("Expected a JSON " + (typeof(T) == typeof(JArray) ? "array" : "object") + " for " + target.Name + ", found " + tok.Type + ".");
            return x;
        }

        static Type GenericInterface(Type t, Type generic)
        {
            if (t.IsGenericType && t.GetGenericTypeDefinition() == generic) return t;
            foreach (Type i in t.GetInterfaces()) if (i.IsGenericType && i.GetGenericTypeDefinition() == generic) return i;
            return null;
        }

        static Type ConcreteType(Type requested, Type fallbackGeneric, Type[] args)
        {
            if (!requested.IsInterface && !requested.IsAbstract) return requested;
            return fallbackGeneric.MakeGenericType(args);
        }
    }

    // ---- text ----

    static class JsonWriter
    {
        internal static string Scalar(JValue v)
        {
            object o = v.Value;
            if (o == null) return "null";
            if (o is bool) return (bool)o ? "true" : "false";
            if (o is double)
            {
                double d = (double)o;
                if (double.IsNaN(d) || double.IsInfinity(d)) return "null";
                string r = d.ToString("R", CultureInfo.InvariantCulture);
                if (r.IndexOf('.') < 0 && r.IndexOf('E') < 0 && r.IndexOf('e') < 0) r += ".0";
                return r;
            }
            IFormattable f = o as IFormattable;
            return f != null ? f.ToString(null, CultureInfo.InvariantCulture) : o.ToString();
        }

        internal static void Write(StringBuilder sb, JToken t, bool indent, int depth)
        {
            JObject o = t as JObject;
            if (o != null)
            {
                if (o.Count == 0) { sb.Append("{}"); return; }
                sb.Append('{');
                bool first = true;
                foreach (KeyValuePair<string, JToken> kv in o)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    if (indent) NewLine(sb, depth + 1);
                    Quote(sb, kv.Key);
                    sb.Append(indent ? ": " : ":");
                    Write(sb, kv.Value, indent, depth + 1);
                }
                if (indent) NewLine(sb, depth);
                sb.Append('}');
                return;
            }
            JArray a = t as JArray;
            if (a != null)
            {
                if (a.Count == 0) { sb.Append("[]"); return; }
                sb.Append('[');
                for (int i = 0; i < a.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    if (indent) NewLine(sb, depth + 1);
                    Write(sb, a[i], indent, depth + 1);
                }
                if (indent) NewLine(sb, depth);
                sb.Append(']');
                return;
            }
            JValue v = (JValue)t;
            if (v.Type == JTokenType.String) Quote(sb, (string)v.Value);
            else sb.Append(Scalar(v));
        }

        static void NewLine(StringBuilder sb, int depth)
        {
            sb.Append(Environment.NewLine);
            sb.Append(' ', depth * 2);
        }

        internal static void Quote(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20 || c == '\u2028' || c == '\u2029') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }

    static class JsonReader
    {
        internal static JToken Parse(string s)
        {
            if (s == null) throw new ArgumentNullException("s");
            int i = 0;
            JToken v = Value(s, ref i, 0);
            Ws(s, ref i);
            if (i != s.Length) throw Fail(s, i, "Additional text after the JSON value");
            return v;
        }

        static JsonException Fail(string s, int i, string what)
        {
            return new JsonException(what + " at position " + i + ".");
        }

        static void Ws(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n' || s[i] == '\uFEFF')) i++;
        }

        static JToken Value(string s, ref int i, int depth)
        {
            if (depth > 256) throw Fail(s, i, "JSON nested too deeply");
            Ws(s, ref i);
            if (i >= s.Length) throw Fail(s, i, "Unexpected end of JSON");
            char c = s[i];
            if (c == '{')
            {
                JObject o = new JObject();
                i++; Ws(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return o; }
                while (true)
                {
                    Ws(s, ref i);
                    if (i >= s.Length || s[i] != '"') throw Fail(s, i, "Expected a property name");
                    string key = Str(s, ref i);
                    Ws(s, ref i);
                    if (i >= s.Length || s[i] != ':') throw Fail(s, i, "Expected ':'");
                    i++;
                    o[key] = Value(s, ref i, depth + 1);
                    Ws(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == '}') { i++; return o; }
                    throw Fail(s, i, "Expected ',' or '}'");
                }
            }
            if (c == '[')
            {
                JArray a = new JArray();
                i++; Ws(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return a; }
                while (true)
                {
                    a.Add(Value(s, ref i, depth + 1));
                    Ws(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == ']') { i++; return a; }
                    throw Fail(s, i, "Expected ',' or ']'");
                }
            }
            if (c == '"') return new JValue(Str(s, ref i));
            if (Word(s, ref i, "true")) return new JValue(true);
            if (Word(s, ref i, "false")) return new JValue(false);
            if (Word(s, ref i, "null")) return JValue.CreateNull();
            int start = i;
            if (i < s.Length && s[i] == '-') i++;
            while (i < s.Length && "0123456789.eE+-".IndexOf(s[i]) >= 0) i++;
            string num = s.Substring(start, i - start);
            if (num.Length == 0 || num == "-") throw Fail(s, start, "Unexpected character '" + c + "'");
            bool isFloat = num.IndexOfAny(new char[] { '.', 'e', 'E' }) >= 0;
            if (!isFloat)
            {
                long l;
                if (long.TryParse(num, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out l)) return new JValue(l);
            }
            double d;
            if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) throw Fail(s, start, "Invalid number '" + num + "'");
            return isFloat ? new JValue(d) : JValue.Number(d, false);
        }

        static bool Word(string s, ref int i, string w)
        {
            if (string.CompareOrdinal(s, i, w, 0, w.Length) != 0) return false;
            i += w.Length;
            return true;
        }

        static string Str(string s, ref int i)
        {
            i++;
            StringBuilder sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length) throw Fail(s, i, "Unterminated string");
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) throw Fail(s, i, "Unterminated string");
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw Fail(s, i, "Bad \\u escape");
                        int code;
                        if (!int.TryParse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code)) throw Fail(s, i, "Bad \\u escape");
                        sb.Append((char)code);
                        i += 4;
                        break;
                    default: throw Fail(s, i - 1, "Bad escape '\\" + e + "'");
                }
            }
        }
    }
}
