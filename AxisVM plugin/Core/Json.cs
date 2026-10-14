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
    /// walks that tree. It is a reader only - nothing here writes JSON.
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
