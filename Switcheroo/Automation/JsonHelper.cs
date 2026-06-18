/*
 * Switcheroo - The incremental-search task switcher for Windows.
 * http://www.switcheroo.io/
 * Copyright 2009, 2010 James Sulak
 * Copyright 2014 Regin Larsen
 *
 * Switcheroo is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * Switcheroo is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with Switcheroo.  If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Switcheroo.Automation
{
    /// <summary>
    /// Tiny JSON reader/writer for the automation protocol. No external dependencies.
    /// </summary>
    internal static class JsonHelper
    {
        public static string Encode(Dictionary<string, object> dict)
        {
            var sb = new StringBuilder();
            sb.Append("{");
            bool first = true;
            foreach (var kvp in dict)
            {
                if (!first) sb.Append(",");
                first = false;
                sb.AppendFormat("\"{0}\":", Escape(kvp.Key));
                sb.Append(EncodeValue(kvp.Value));
            }
            sb.Append("}");
            return sb.ToString();
        }

        public static Dictionary<string, string> Decode(string json)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int i = 0;
            SkipWhitespace(json, ref i);
            if (i >= json.Length || json[i] != '{')
                return result;
            i++;
            while (i < json.Length)
            {
                SkipWhitespace(json, ref i);
                if (i < json.Length && json[i] == '}')
                    break;
                var key = ParseString(json, ref i);
                SkipWhitespace(json, ref i);
                if (i < json.Length && json[i] == ':') i++;
                SkipWhitespace(json, ref i);
                var value = ParseValue(json, ref i);
                result[key] = value;
                SkipWhitespace(json, ref i);
                if (i < json.Length && json[i] == ',') i++;
            }
            return result;
        }

        private static string EncodeValue(object value)
        {
            if (value == null) return "null";
            if (value is bool b) return b ? "true" : "false";
            if (value is int || value is long || value is double || value is float) return value.ToString();
            if (value is string s) return "\"" + Escape(s) + "\"";
            // Encode dictionaries (Dictionary<string,string>, Dictionary<string,object>, etc.)
            // as JSON objects. Without this, Dictionary<string,string> fell through to the
            // IEnumerable branch and was serialized as an array of "[key, value]" strings.
            if (value is System.Collections.IDictionary dict)
            {
                var sb = new StringBuilder();
                sb.Append("{");
                bool first = true;
                foreach (System.Collections.DictionaryEntry entry in dict)
                {
                    if (!first) sb.Append(",");
                    first = false;
                    sb.AppendFormat("\"{0}\":", Escape(Convert.ToString(entry.Key, System.Globalization.CultureInfo.InvariantCulture)));
                    sb.Append(EncodeValue(entry.Value));
                }
                sb.Append("}");
                return sb.ToString();
            }
            if (value is Dictionary<string, object> dictObj) return Encode(dictObj);
            if (value is System.Collections.IEnumerable enumerable)
            {
                var sb = new StringBuilder();
                sb.Append("[");
                bool first = true;
                foreach (var item in enumerable)
                {
                    if (!first) sb.Append(",");
                    first = false;
                    sb.Append(EncodeValue(item));
                }
                sb.Append("]");
                return sb.ToString();
            }
            return "\"" + Escape(value.ToString()) + "\"";
        }

        private static string Escape(string s)
        {
            return s
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t");
        }

        private static void SkipWhitespace(string json, ref int i)
        {
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
        }

        private static string ParseString(string json, ref int i)
        {
            SkipWhitespace(json, ref i);
            if (i >= json.Length || json[i] != '\"') return string.Empty;
            i++;
            var sb = new StringBuilder();
            while (i < json.Length)
            {
                char c = json[i];
                if (c == '\"') { i++; break; }
                if (c == '\\' && i + 1 < json.Length)
                {
                    i++;
                    switch (json[i])
                    {
                        case '\"': sb.Append('\"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        default: sb.Append(json[i]); break;
                    }
                    i++;
                }
                else
                {
                    sb.Append(c);
                    i++;
                }
            }
            return sb.ToString();
        }

        private static string ParseValue(string json, ref int i)
        {
            SkipWhitespace(json, ref i);
            if (i >= json.Length) return string.Empty;
            if (json[i] == '\"')
                return ParseString(json, ref i);
            if (json[i] == '{')
            {
                int start = i;
                int depth = 0;
                bool inString = false;
                while (i < json.Length)
                {
                    char c = json[i];
                    if (c == '\"' && (i == 0 || json[i - 1] != '\\'))
                        inString = !inString;
                    else if (!inString)
                    {
                        if (c == '{') depth++;
                        else if (c == '}') { depth--; i++; if (depth == 0) break; }
                    }
                    i++;
                }
                return json.Substring(start, i - start);
            }
            if (json[i] == '[')
            {
                int start = i;
                int depth = 0;
                bool inString = false;
                while (i < json.Length)
                {
                    char c = json[i];
                    if (c == '\"' && (i == 0 || json[i - 1] != '\\'))
                        inString = !inString;
                    else if (!inString)
                    {
                        if (c == '[') depth++;
                        else if (c == ']') { depth--; i++; if (depth == 0) break; }
                    }
                    i++;
                }
                return json.Substring(start, i - start);
            }
            int tokenStart = i;
            while (i < json.Length && json[i] != ',' && json[i] != '}' && !char.IsWhiteSpace(json[i]))
                i++;
            return json.Substring(tokenStart, i - tokenStart);
        }
    }
}
