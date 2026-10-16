using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AxisVM_plugin.Core
{
    /// <summary>
    /// A minimal, dependency-free JSON reader.
    ///
    /// The other six addons deploy as a single DLL dropped into C:\AXISVM X8\AddOns. Pulling in
    /// Newtonsoft.Json would mean a second file to copy and a version that could clash with whatever
    /// AxisVM itself has already loaded into the process, so the ~200 lines below buy back a
    /// single-file deployment. It parses the whole document into a JsonValue tree; RhinoModelDto then
    /// walks that tree. The writer at the end exists for the live link file (LinkFile.cs), which the
    /// addon rewrites while keeping the Rhino section exactly as Rhino wrote it.
    /// </summary>
    public class JsonValue
    {
        public enum JsonType { Null, Bool, Number, String, Array, Object }

        public JsonType Type;
        public bool BoolValue;
        public double NumberValue;
        public string StringValue;
        public List<JsonValue> ArrayValue;
        public Dictionary<string, JsonValue> ObjectValue;

        public bool IsNull { get { return Type == JsonType.Null; } }

        /// <summary>Member lookup on an object. Returns null when absent, so callers can test in one step.</summary>
        public JsonValue this[string key]
        {
            get
            {
                if (Type != JsonType.Object || ObjectValue == null) return null;
                JsonValue v;
                return ObjectValue.TryGetValue(key, out v) ? v : null;
            }
        }

        /// <summary>Element count for an array, member count for an object, 0 for anything else.</summary>
        public int Count
        {
            get
            {
                if (Type == JsonType.Array && ArrayValue != null) return ArrayValue.Count;
                if (Type == JsonType.Object && ObjectValue != null) return ObjectValue.Count;
                return 0;
            }
        }

        public JsonValue At(int index)
        {
            if (Type != JsonType.Array || ArrayValue == null) return null;
            if (index < 0 || index >= ArrayValue.Count) return null;
            return ArrayValue[index];
        }

        /// <summary>String value, or <paramref name="fallback"/> when the node is missing or not a string.</summary>
        public string AsString(string fallback)
        {
            if (Type == JsonType.String) return StringValue;
            if (Type == JsonType.Number) return NumberValue.ToString(CultureInfo.InvariantCulture);
            if (Type == JsonType.Bool) return BoolValue ? "true" : "false";
            return fallback;
        }

        public double AsDouble(double fallback)
        {
            if (Type == JsonType.Number) return NumberValue;
            if (Type == JsonType.String)
            {
                double d;
                if (double.TryParse(StringValue, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            }
            return fallback;
        }

        public int AsInt(int fallback)
        {
            double d = AsDouble(double.NaN);
            return double.IsNaN(d) ? fallback : (int)Math.Round(d);
        }

        // ==================================================================
        // Building and writing
        // ==================================================================

        public static JsonValue Obj() { return new JsonValue { Type = JsonType.Object, ObjectValue = new Dictionary<string, JsonValue>(StringComparer.Ordinal) }; }
        public static JsonValue Arr() { return new JsonValue { Type = JsonType.Array, ArrayValue = new List<JsonValue>() }; }
        public static JsonValue Str(string s) { return s == null ? new JsonValue() : new JsonValue { Type = JsonType.String, StringValue = s }; }
        public static JsonValue Num(double d) { return new JsonValue { Type = JsonType.Number, NumberValue = d }; }
        public static JsonValue Bool(bool b) { return new JsonValue { Type = JsonType.Bool, BoolValue = b }; }

        public static JsonValue Xyz(double x, double y, double z)
        {
            JsonValue a = Arr();
            a.Add(Num(x)).Add(Num(y)).Add(Num(z));
            return a;
        }

        /// <summary>Sets a member of an object (returns this, for chaining).</summary>
        public JsonValue Set(string key, JsonValue value)
        {
            if (Type != JsonType.Object) throw new InvalidOperationException("Set on a JSON value that is not an object.");
            ObjectValue[key] = value ?? new JsonValue();
            return this;
        }

        /// <summary>Appends to an array (returns this, for chaining).</summary>
        public JsonValue Add(JsonValue value)
        {
            if (Type != JsonType.Array) throw new InvalidOperationException("Add on a JSON value that is not an array.");
            ArrayValue.Add(value ?? new JsonValue());
            return this;
        }

        public string ToJson()
        {
            StringBuilder sb = new StringBuilder();
            Write(sb, 0);
            return sb.ToString();
        }

        private void Write(StringBuilder sb, int indent)
        {
            switch (Type)
            {
                case JsonType.Null: sb.Append("null"); break;
                case JsonType.Bool: sb.Append(BoolValue ? "true" : "false"); break;
                case JsonType.Number:
                    sb.Append(double.IsNaN(NumberValue) || double.IsInfinity(NumberValue) ? "0" : NumberValue.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case JsonType.String: WriteString(sb, StringValue ?? ""); break;
                case JsonType.Array:
                    // Short arrays of numbers (coordinates) stay on one line.
                    bool flat = ArrayValue.Count <= 4 && ArrayValue.TrueForAll(v => v.Type == JsonType.Number);
                    sb.Append('[');
                    for (int i = 0; i < ArrayValue.Count; i++)
                    {
                        if (i > 0) sb.Append(flat ? ", " : ",");
                        if (!flat) NewLine(sb, indent + 1);
                        ArrayValue[i].Write(sb, indent + 1);
                    }
                    if (!flat && ArrayValue.Count > 0) NewLine(sb, indent);
                    sb.Append(']');
                    break;
                case JsonType.Object:
                    sb.Append('{');
                    bool first = true;
                    foreach (KeyValuePair<string, JsonValue> kv in ObjectValue)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        NewLine(sb, indent + 1);
                        WriteString(sb, kv.Key);
                        sb.Append(": ");
                        kv.Value.Write(sb, indent + 1);
                    }
                    if (!first) NewLine(sb, indent);
                    sb.Append('}');
                    break;
            }
        }

        private static void NewLine(StringBuilder sb, int indent)
        {
            sb.Append("\r\n");
            sb.Append(' ', indent * 2);
        }

        private static void WriteString(StringBuilder sb, string s)
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
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ==================================================================
        // Parsing
        // ==================================================================

        /// <summary>
        /// Parses a complete JSON document. Throws <see cref="FormatException"/> with a character
        /// offset on malformed input - the offset is what makes a hand-edited export file
        /// debuggable, so it is always included in the message.
        /// </summary>
        public static JsonValue Parse(string text)
        {
            if (text == null) throw new FormatException("The JSON file is empty.");

            int pos = 0;
            JsonValue root = ParseValue(text, ref pos);
            SkipWhitespace(text, ref pos);
            if (pos < text.Length)
                throw new FormatException(string.Format("Unexpected text after the end of the JSON document, at character {0}.", pos));

            return root;
        }

        private static JsonValue ParseValue(string s, ref int pos)
        {
            SkipWhitespace(s, ref pos);
            if (pos >= s.Length) throw new FormatException("The JSON document ended earlier than expected.");

            char c = s[pos];
            switch (c)
            {
                case '{': return ParseObject(s, ref pos);
                case '[': return ParseArray(s, ref pos);
                case '"': return new JsonValue { Type = JsonType.String, StringValue = ParseString(s, ref pos) };
                case 't': Expect(s, ref pos, "true"); return new JsonValue { Type = JsonType.Bool, BoolValue = true };
                case 'f': Expect(s, ref pos, "false"); return new JsonValue { Type = JsonType.Bool, BoolValue = false };
                case 'n': Expect(s, ref pos, "null"); return new JsonValue { Type = JsonType.Null };
                default: return ParseNumber(s, ref pos);
            }
        }

        private static JsonValue ParseObject(string s, ref int pos)
        {
            JsonValue result = new JsonValue
            {
                Type = JsonType.Object,
                ObjectValue = new Dictionary<string, JsonValue>(StringComparer.Ordinal)
            };

            pos++; // '{'
            SkipWhitespace(s, ref pos);
            if (pos < s.Length && s[pos] == '}') { pos++; return result; }

            while (true)
            {
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length || s[pos] != '"')
                    throw new FormatException(string.Format("Expected a member name at character {0}.", pos));

                string key = ParseString(s, ref pos);

                SkipWhitespace(s, ref pos);
                if (pos >= s.Length || s[pos] != ':')
                    throw new FormatException(string.Format("Expected ':' after member name '{0}' at character {1}.", key, pos));
                pos++;

                // A duplicate key keeps the last value, which is what every mainstream parser does.
                result.ObjectValue[key] = ParseValue(s, ref pos);

                SkipWhitespace(s, ref pos);
                if (pos >= s.Length) throw new FormatException("The JSON object was never closed.");
                if (s[pos] == ',') { pos++; continue; }
                if (s[pos] == '}') { pos++; return result; }
                throw new FormatException(string.Format("Expected ',' or '}}' at character {0}.", pos));
            }
        }

        private static JsonValue ParseArray(string s, ref int pos)
        {
            JsonValue result = new JsonValue { Type = JsonType.Array, ArrayValue = new List<JsonValue>() };

            pos++; // '['
            SkipWhitespace(s, ref pos);
            if (pos < s.Length && s[pos] == ']') { pos++; return result; }

            while (true)
            {
                result.ArrayValue.Add(ParseValue(s, ref pos));

                SkipWhitespace(s, ref pos);
                if (pos >= s.Length) throw new FormatException("The JSON array was never closed.");
                if (s[pos] == ',') { pos++; continue; }
                if (s[pos] == ']') { pos++; return result; }
                throw new FormatException(string.Format("Expected ',' or ']' at character {0}.", pos));
            }
        }

        private static string ParseString(string s, ref int pos)
        {
            pos++; // opening quote
            StringBuilder sb = new StringBuilder();

            while (pos < s.Length)
            {
                char c = s[pos++];

                if (c == '"') return sb.ToString();

                if (c != '\\') { sb.Append(c); continue; }

                if (pos >= s.Length) break;
                char esc = s[pos++];
                switch (esc)
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
                        if (pos + 4 > s.Length)
                            throw new FormatException(string.Format("Truncated \\u escape at character {0}.", pos));
                        int code = int.Parse(s.Substring(pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        sb.Append((char)code);
                        pos += 4;
                        break;
                    default:
                        throw new FormatException(string.Format("Unknown escape '\\{0}' at character {1}.", esc, pos - 1));
                }
            }

            throw new FormatException("A JSON string was never closed.");
        }

        private static JsonValue ParseNumber(string s, ref int pos)
        {
            int start = pos;
            if (pos < s.Length && (s[pos] == '-' || s[pos] == '+')) pos++;
            while (pos < s.Length && (char.IsDigit(s[pos]) || s[pos] == '.' || s[pos] == 'e' || s[pos] == 'E' ||
                                      ((s[pos] == '-' || s[pos] == '+') && (s[pos - 1] == 'e' || s[pos - 1] == 'E'))))
                pos++;

            string raw = s.Substring(start, pos - start);
            double value;
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                throw new FormatException(string.Format("'{0}' is not a number, at character {1}.", raw, start));

            return new JsonValue { Type = JsonType.Number, NumberValue = value };
        }

        private static void Expect(string s, ref int pos, string literal)
        {
            if (pos + literal.Length > s.Length || string.CompareOrdinal(s, pos, literal, 0, literal.Length) != 0)
                throw new FormatException(string.Format("Expected '{0}' at character {1}.", literal, pos));
            pos += literal.Length;
        }

        private static void SkipWhitespace(string s, ref int pos)
        {
            while (pos < s.Length && (s[pos] == ' ' || s[pos] == '\t' || s[pos] == '\r' || s[pos] == '\n'))
                pos++;
        }
    }
}
