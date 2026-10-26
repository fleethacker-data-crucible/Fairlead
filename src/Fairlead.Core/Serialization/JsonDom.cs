using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Fairlead.Core.Serialization
{
    // A small JSON document model with the subset of the Newtonsoft.Json.Linq API Fairlead used (JObject, JArray,
    // JValue, conversions), so the engine needs no third-party assembly. Text output matches Newtonsoft's byte for
    // byte for everything Fairlead writes. One deliberate difference: strings that look like dates stay strings
    // (Newtonsoft turned them into DateTime values and re-formatted them on the way out). As in Newtonsoft, a token has
    // at most one parent: putting a token that already sits in an object or array into another one stores a deep copy,
    // so a translated request never shares (and later edits) a node of the request it came from.

    public enum JTokenType
    {
        None = 0,
        Object = 1,
        Array = 2,
        Integer = 6,
        Float = 7,
        String = 8,
        Boolean = 9,
        Null = 10,
    }

    public enum Formatting
    {
        None = 0,
        Indented = 1,
    }

    /// <summary>Malformed JSON text, or JSON that does not fit the requested type.</summary>
    public class JsonException : Exception
    {
        public JsonException(string message) : base(message) { }
        public JsonException(string message, Exception inner) : base(message, inner) { }
    }

    public abstract class JToken : IEnumerable<JToken>
    {
        public abstract JTokenType Type { get; }

        /// <summary>The object or array holding this token, if any.</summary>
        public JToken Parent { get; private set; }

        /// <summary>Takes <paramref name="child"/> into this container: null becomes a JSON null, a token that already has a parent is copied.</summary>
        internal JToken Adopt(JToken child)
        {
            if (child == null) child = JValue.CreateNull();
            if (child.Parent != null) child = child.DeepClone();
            child.Parent = this;
            return child;
        }

        internal static void Release(JToken child)
        {
            if (child != null) child.Parent = null;
        }

        /// <summary>Child by property name (objects) or index (arrays). Missing object properties read as null.</summary>
        public virtual JToken this[object key]
        {
            get { throw new InvalidOperationException("Cannot access child value on " + GetType().Name + "."); }
            set { throw new InvalidOperationException("Cannot set child value on " + GetType().Name + "."); }
        }

        public virtual bool HasValues { get { return false; } }

        public virtual IEnumerator<JToken> GetEnumerator()
        {
            return Enumerable.Empty<JToken>().GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        /// <summary>Indented JSON text.</summary>
        public override string ToString()
        {
            return ToString(Formatting.Indented);
        }

        public string ToString(Formatting formatting)
        {
            return JsonWriter.Write(this, formatting);
        }

        public static JToken Parse(string json)
        {
            return JsonReader.Parse(json);
        }

        public static JToken FromObject(object value)
        {
            return JsonSerializer.ToToken(value, JsonSettings.Default);
        }

        public static JToken FromObject(object value, JsonSettings settings)
        {
            return JsonSerializer.ToToken(value, settings);
        }

        public T ToObject<T>()
        {
            return (T)JsonSerializer.FromToken(this, typeof(T), JsonSettings.Default, null);
        }

        public T ToObject<T>(JsonSettings settings)
        {
            return (T)JsonSerializer.FromToken(this, typeof(T), settings, null);
        }

        public object ToObject(Type type)
        {
            return JsonSerializer.FromToken(this, type, JsonSettings.Default, null);
        }

        public virtual JToken DeepClone()
        {
            return Parse(ToString(Formatting.None));
        }

        /// <summary>The child at <paramref name="key"/> converted to <typeparamref name="T"/>; a missing child or JSON null gives default(T), like Newtonsoft's Value&lt;T&gt;().</summary>
        public T Value<T>(object key)
        {
            var token = this[key];
            if (token == null) return default(T);
            if (token is T) return (T)(object)token;
            if (token.Type == JTokenType.Null)
            {
                if (default(T) == null) return default(T);
                throw new InvalidCastException("Can not convert Null to " + typeof(T).Name + ".");
            }
            var v = token as JValue;
            if (v == null) throw new InvalidCastException("Cannot cast " + token.GetType().Name + " to " + typeof(T).Name + ".");
            var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
            if (target == typeof(string)) return (T)(object)(string)token;
            if (target.IsInstanceOfType(v.Value)) return (T)v.Value;
            return (T)Convert.ChangeType(v.Value, target, CultureInfo.InvariantCulture);
        }

        /// <summary>A token for any CLR value: tokens pass through, null becomes a JSON null.</summary>
        internal static JToken From(object value)
        {
            if (value == null) return JValue.CreateNull();
            var token = value as JToken;
            if (token != null) return token;
            return JsonSerializer.ToToken(value, JsonSettings.Default);
        }

        // ---- conversions to CLR values (what Newtonsoft's explicit operators did) ----

        private static JValue ValueOf(JToken token, string target)
        {
            var v = token as JValue;
            if (v == null) throw new ArgumentException("Can not convert " + token.Type + " to " + target + ".");
            return v;
        }

        private static bool IsNull(JToken token)
        {
            return token == null || token.Type == JTokenType.Null;
        }

        public static explicit operator string(JToken token)
        {
            if (IsNull(token)) return null;
            var v = ValueOf(token, "String");
            if (v.Value is string) return (string)v.Value;
            if (v.Value is byte[]) return Convert.ToBase64String((byte[])v.Value);
            return Convert.ToString(v.Value, CultureInfo.InvariantCulture);
        }

        public static explicit operator bool(JToken token)
        {
            if (token == null) throw new ArgumentNullException("token");
            return Convert.ToBoolean(ValueOf(token, "Boolean").NonNull("Boolean"), CultureInfo.InvariantCulture);
        }

        public static explicit operator bool?(JToken token)
        {
            if (IsNull(token)) return null;
            return Convert.ToBoolean(ValueOf(token, "Boolean").Value, CultureInfo.InvariantCulture);
        }

        public static explicit operator int(JToken token)
        {
            if (token == null) throw new ArgumentNullException("token");
            return Convert.ToInt32(ValueOf(token, "Int32").NonNull("Int32"), CultureInfo.InvariantCulture);
        }

        public static explicit operator int?(JToken token)
        {
            if (IsNull(token)) return null;
            return Convert.ToInt32(ValueOf(token, "Int32").Value, CultureInfo.InvariantCulture);
        }

        public static explicit operator long(JToken token)
        {
            if (token == null) throw new ArgumentNullException("token");
            return Convert.ToInt64(ValueOf(token, "Int64").NonNull("Int64"), CultureInfo.InvariantCulture);
        }

        public static explicit operator long?(JToken token)
        {
            if (IsNull(token)) return null;
            return Convert.ToInt64(ValueOf(token, "Int64").Value, CultureInfo.InvariantCulture);
        }

        public static explicit operator double(JToken token)
        {
            if (token == null) throw new ArgumentNullException("token");
            return Convert.ToDouble(ValueOf(token, "Double").NonNull("Double"), CultureInfo.InvariantCulture);
        }

        public static explicit operator double?(JToken token)
        {
            if (IsNull(token)) return null;
            return Convert.ToDouble(ValueOf(token, "Double").Value, CultureInfo.InvariantCulture);
        }

        public static explicit operator decimal?(JToken token)
        {
            if (IsNull(token)) return null;
            return Convert.ToDecimal(ValueOf(token, "Decimal").Value, CultureInfo.InvariantCulture);
        }

        public static explicit operator DateTimeOffset?(JToken token)
        {
            if (IsNull(token)) return null;
            var v = ValueOf(token, "DateTimeOffset").Value;
            if (v is DateTimeOffset) return (DateTimeOffset)v;
            if (v is DateTime) return new DateTimeOffset((DateTime)v);
            return DateTimeOffset.Parse(Convert.ToString(v, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        }

        // ---- CLR values to tokens ----

        public static implicit operator JToken(string value) { return new JValue(value); }
        public static implicit operator JToken(bool value) { return new JValue(value); }
        public static implicit operator JToken(int value) { return new JValue(value); }
        public static implicit operator JToken(long value) { return new JValue(value); }
        public static implicit operator JToken(double value) { return new JValue(value); }
        public static implicit operator JToken(decimal value) { return new JValue(value); }
        public static implicit operator JToken(bool? value) { return new JValue(value); }
        public static implicit operator JToken(int? value) { return new JValue(value); }
        public static implicit operator JToken(long? value) { return new JValue(value); }
        public static implicit operator JToken(double? value) { return new JValue(value); }
    }

    /// <summary>A string, number, boolean or null.</summary>
    public sealed class JValue : JToken
    {
        private readonly object _value;
        private readonly JTokenType _type;

        public JValue(object value)
        {
            _value = Normalize(value);
            _type = TypeOf(_value);
        }

        private JValue(object value, JTokenType type)
        {
            _value = value;
            _type = type;
        }

        public static JValue CreateNull()
        {
            return new JValue(null, JTokenType.Null);
        }

        public static JValue CreateString(string value)
        {
            return new JValue(value);
        }

        internal static JValue Integer(object value)
        {
            return new JValue(value, JTokenType.Integer);
        }

        public object Value { get { return _value; } }
        public override JTokenType Type { get { return _type; } }

        internal object NonNull(string target)
        {
            if (_value == null) throw new ArgumentException("Can not convert Null to " + target + ".");
            return _value;
        }

        private static object Normalize(object value)
        {
            if (value is int || value is short || value is sbyte || value is byte || value is ushort || value is uint) return Convert.ToInt64(value, CultureInfo.InvariantCulture);
            if (value is char) return value.ToString();
            return value;
        }

        private static JTokenType TypeOf(object value)
        {
            if (value == null) return JTokenType.Null;
            if (value is string) return JTokenType.String;
            if (value is bool) return JTokenType.Boolean;
            if (value is long || value is ulong) return JTokenType.Integer;
            if (value is double || value is float || value is decimal) return JTokenType.Float;
            return JTokenType.String;
        }

        /// <summary>The value as text in the current culture (empty for null), like Newtonsoft's JValue.ToString().</summary>
        public override string ToString()
        {
            if (_value == null) return string.Empty;
            return Convert.ToString(_value, CultureInfo.CurrentCulture);
        }

        public override JToken DeepClone()
        {
            return new JValue(_value, _type);
        }

        public override bool Equals(object obj)
        {
            var other = obj as JValue;
            if (other == null) return false;
            if (_value == null || other._value == null) return _value == null && other._value == null;
            if (_type != other._type) return false;
            if (_type == JTokenType.Integer || _type == JTokenType.Float) return Convert.ToDouble(_value, CultureInfo.InvariantCulture) == Convert.ToDouble(other._value, CultureInfo.InvariantCulture);
            return _value.Equals(other._value);
        }

        public override int GetHashCode()
        {
            return _value == null ? 0 : _value.GetHashCode();
        }
    }

    /// <summary>A name/value pair of a JObject.</summary>
    public sealed class JProperty : JToken
    {
        private readonly string _name;

        public JProperty(string name, object content)
        {
            if (name == null) throw new ArgumentNullException("name");
            _name = name;
            Value = From(content);
        }

        public string Name { get { return _name; } }
        public JToken Value { get; internal set; }
        public override JTokenType Type { get { return JTokenType.None; } }

        public override IEnumerator<JToken> GetEnumerator()
        {
            return new List<JToken> { Value }.GetEnumerator();
        }
    }

    /// <summary>An ordered set of named values. Reading a missing name gives null; setting a name keeps its position.</summary>
    public sealed class JObject : JToken
    {
        private readonly List<JProperty> _properties = new List<JProperty>();
        private readonly Dictionary<string, JProperty> _index = new Dictionary<string, JProperty>(StringComparer.Ordinal);

        public JObject() { }

        /// <summary>Properties, or sequences of properties (as Newtonsoft's <c>new JObject(content)</c>).</summary>
        public JObject(params object[] content)
        {
            if (content == null) return;
            foreach (var c in content) AddContent(c);
        }

        private void AddContent(object content)
        {
            if (content == null) return;
            var p = content as JProperty;
            if (p != null) { Add(p.Name, p.Value); return; }
            var seq = content as IEnumerable;
            if (seq != null && !(content is JToken) && !(content is string))
            {
                foreach (var c in seq) AddContent(c);
                return;
            }
            throw new ArgumentException("Can not add " + content.GetType().Name + " to JObject.");
        }

        public override JTokenType Type { get { return JTokenType.Object; } }
        public int Count { get { return _properties.Count; } }
        public override bool HasValues { get { return _properties.Count > 0; } }

        public JToken this[string name]
        {
            get
            {
                JProperty p;
                return name != null && _index.TryGetValue(name, out p) ? p.Value : null;
            }
            set
            {
                JProperty p;
                if (_index.TryGetValue(name, out p))
                {
                    if (ReferenceEquals(p.Value, value)) return;
                    var adopted = Adopt(value);
                    Release(p.Value);
                    p.Value = adopted;
                    return;
                }
                Add(name, value);
            }
        }

        public override JToken this[object key]
        {
            get
            {
                var name = key as string;
                if (name == null) throw new ArgumentException("Accessed JObject values with invalid key value: " + (key ?? "null") + ". Object property name expected.");
                return this[name];
            }
            set
            {
                var name = key as string;
                if (name == null) throw new ArgumentException("Set JObject values with invalid key value: " + (key ?? "null") + ". Object property name expected.");
                this[name] = value;
            }
        }

        public void Add(string name, JToken value)
        {
            if (_index.ContainsKey(name)) throw new ArgumentException("Can not add property " + name + " to JObject. Property with the same name already exists on object.");
            var p = new JProperty(name, Adopt(value));
            _properties.Add(p);
            _index[name] = p;
        }

        public bool Remove(string name)
        {
            JProperty p;
            if (name == null || !_index.TryGetValue(name, out p)) return false;
            _index.Remove(name);
            _properties.Remove(p);
            Release(p.Value);
            return true;
        }

        public bool ContainsKey(string name)
        {
            return name != null && _index.ContainsKey(name);
        }

        public bool TryGetValue(string name, out JToken value)
        {
            JProperty p;
            if (name != null && _index.TryGetValue(name, out p)) { value = p.Value; return true; }
            value = null;
            return false;
        }

        public JProperty Property(string name)
        {
            JProperty p;
            return name != null && _index.TryGetValue(name, out p) ? p : null;
        }

        public IEnumerable<JProperty> Properties()
        {
            return _properties.ToList();
        }

        /// <summary>Enumerates the properties (as JProperty tokens), like Newtonsoft's JToken view of an object.</summary>
        public override IEnumerator<JToken> GetEnumerator()
        {
            return _properties.Cast<JToken>().ToList().GetEnumerator();
        }

        public static new JObject Parse(string json)
        {
            var token = JsonReader.Parse(json);
            var o = token as JObject;
            if (o == null) throw new JsonException("Error reading JObject from JsonReader. Current JsonReader item is not an object: " + token.Type + ".");
            return o;
        }

        public static new JObject FromObject(object value)
        {
            return FromObject(value, JsonSettings.Default);
        }

        public static new JObject FromObject(object value, JsonSettings settings)
        {
            var o = JsonSerializer.ToToken(value, settings) as JObject;
            if (o == null) throw new ArgumentException("Object serialized to " + (value == null ? "Null" : "a value") + ". JObject instance expected.");
            return o;
        }

        public override JToken DeepClone()
        {
            var o = new JObject();
            foreach (var p in _properties) o.Add(p.Name, p.Value.DeepClone());
            return o;
        }
    }

    /// <summary>An ordered list of tokens.</summary>
    public sealed class JArray : JToken, IList<JToken>
    {
        private readonly List<JToken> _items = new List<JToken>();

        public JArray() { }

        /// <summary>Values, tokens or sequences of them (sequences are flattened, as Newtonsoft did).</summary>
        public JArray(params object[] content)
        {
            if (content == null) return;
            foreach (var c in content) Add(c);
        }

        public JArray(object content)
        {
            Add(content);
        }

        public override JTokenType Type { get { return JTokenType.Array; } }
        public int Count { get { return _items.Count; } }
        public bool IsReadOnly { get { return false; } }
        public override bool HasValues { get { return _items.Count > 0; } }

        public JToken this[int index]
        {
            get { return _items[index]; }
            set
            {
                if (ReferenceEquals(_items[index], value)) return;
                var adopted = Adopt(value);
                Release(_items[index]);
                _items[index] = adopted;
            }
        }

        public override JToken this[object key]
        {
            get
            {
                if (!(key is int)) throw new ArgumentException("Accessed JArray values with invalid key value: " + (key ?? "null") + ". Int32 array index expected.");
                return this[(int)key];
            }
            set
            {
                if (!(key is int)) throw new ArgumentException("Set JArray values with invalid key value: " + (key ?? "null") + ". Int32 array index expected.");
                this[(int)key] = value;
            }
        }

        public void Add(object content)
        {
            var token = content as JToken;
            if (token != null) { _items.Add(Adopt(token)); return; }
            var seq = content as IEnumerable;
            if (content != null && seq != null && !(content is string) && !(content is byte[]))
            {
                foreach (var c in seq) _items.Add(Adopt(From(c)));
                return;
            }
            _items.Add(Adopt(From(content)));
        }

        public void Add(JToken item)
        {
            _items.Add(Adopt(item));
        }

        public void Insert(int index, JToken item) { _items.Insert(index, Adopt(item)); }
        public void RemoveAt(int index) { Release(_items[index]); _items.RemoveAt(index); }
        public bool Remove(JToken item)
        {
            if (!_items.Remove(item)) return false;
            Release(item);
            return true;
        }
        public void Clear()
        {
            foreach (var i in _items) Release(i);
            _items.Clear();
        }
        public bool Contains(JToken item) { return _items.Contains(item); }
        public int IndexOf(JToken item) { return _items.IndexOf(item); }
        public void CopyTo(JToken[] array, int arrayIndex) { _items.CopyTo(array, arrayIndex); }

        public override IEnumerator<JToken> GetEnumerator()
        {
            return _items.ToList().GetEnumerator();
        }

        public static new JArray Parse(string json)
        {
            var token = JsonReader.Parse(json);
            var a = token as JArray;
            if (a == null) throw new JsonException("Error reading JArray from JsonReader. Current JsonReader item is not an array: " + token.Type + ".");
            return a;
        }

        public static new JArray FromObject(object value)
        {
            var a = JsonSerializer.ToToken(value, JsonSettings.Default) as JArray;
            if (a == null) throw new ArgumentException("Object serialized to a non-array. JArray instance expected.");
            return a;
        }

        public override JToken DeepClone()
        {
            var a = new JArray();
            foreach (var i in _items) a.Add(i.DeepClone());
            return a;
        }
    }

    /// <summary>C# 5 stand-in for the null-conditional index (<c>token?["name"]</c>, <c>token?[0]</c>).</summary>
    public static class JTokenExtensions
    {
        /// <summary>The child at <paramref name="key"/>, or null when <paramref name="token"/> is null.</summary>
        public static JToken At(this JToken token, object key)
        {
            return token == null ? null : token[key];
        }
    }
}
