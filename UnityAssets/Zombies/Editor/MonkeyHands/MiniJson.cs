using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ZombiesMonkeyHands.Editor
{
    /// <summary>
    /// Minimal JSON reader for the reference dumps (objects become Dictionary&lt;string, object&gt;, arrays List&lt;object&gt;,
    /// numbers double). Unity's JsonUtility can't read dictionaries or untyped trees.
    /// </summary>
    public static class MiniJson
    {
        public static object Parse(string json)
        {
            int i = 0;
            object value = ReadValue(json, ref i);
            return value;
        }

        private static object ReadValue(string s, ref int i)
        {
            SkipWhitespace(s, ref i);
            char c = s[i];
            if (c == '{') return ReadObject(s, ref i);
            if (c == '[') return ReadArray(s, ref i);
            if (c == '"') return ReadString(s, ref i);
            if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (s.Length - i >= 5 && string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            return ReadNumber(s, ref i);
        }

        private static Dictionary<string, object> ReadObject(string s, ref int i)
        {
            var result = new Dictionary<string, object>();
            i++; // {
            SkipWhitespace(s, ref i);
            if (s[i] == '}') { i++; return result; }
            while (true)
            {
                SkipWhitespace(s, ref i);
                string key = ReadString(s, ref i);
                SkipWhitespace(s, ref i);
                i++; // :
                result[key] = ReadValue(s, ref i);
                SkipWhitespace(s, ref i);
                if (s[i++] == '}') return result;
            }
        }

        private static List<object> ReadArray(string s, ref int i)
        {
            var result = new List<object>();
            i++; // [
            SkipWhitespace(s, ref i);
            if (s[i] == ']') { i++; return result; }
            while (true)
            {
                result.Add(ReadValue(s, ref i));
                SkipWhitespace(s, ref i);
                if (s[i++] == ']') return result;
            }
        }

        private static string ReadString(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++; // opening quote
            while (true)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u': sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16)); i += 4; break;
                    default: sb.Append(e); break;
                }
            }
        }

        private static double ReadNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0)
                i++;
            string text = s.Substring(start, i - start);
            // Python's json writes NaN/Infinity bare
            if (text.Length == 0)
            {
                if (string.CompareOrdinal(s, i, "NaN", 0, 3) == 0) { i += 3; return double.NaN; }
                if (string.CompareOrdinal(s, i, "Infinity", 0, 8) == 0) { i += 8; return double.PositiveInfinity; }
                if (string.CompareOrdinal(s, i, "-Infinity", 0, 9) == 0) { i += 9; return double.NegativeInfinity; }
                throw new FormatException($"Unexpected character '{s[i]}' at {i}");
            }
            return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i]))
                i++;
        }

        // ---------- Access helpers ----------

        public static Dictionary<string, object> Obj(this object o) => (Dictionary<string, object>)o;
        public static List<object> Arr(this object o) => (List<object>)o;
        public static object Get(this object o, string key) => o is Dictionary<string, object> d && d.TryGetValue(key, out object v) ? v : null;
        public static string Str(this object o, string key) => o.Get(key) as string;
        public static float Num(this object o, string key, float fallback = 0f) => o.Get(key) is double d ? (float)d : o.Get(key) is bool b ? (b ? 1f : 0f) : fallback;
        public static int Int(this object o, string key, int fallback = 0) => o.Get(key) is double d ? (int)(long)d : fallback;
        public static bool Bool(this object o, string key) => o.Get(key) is bool b ? b : o.Get(key) is double d && d != 0;
    }
}
