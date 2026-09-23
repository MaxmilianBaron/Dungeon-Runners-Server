using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Numerics;
using DungeonRunners.Engine;

namespace DungeonRunners.Data
{

    public class GCNode
    {
        public string Name { get; set; }
        public string Extends { get; set; }
        public bool IsStatic { get; set; }
        public bool IsAnonymous { get; set; }

        public Dictionary<string, string> Properties { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public List<string> PropertyOrder { get; set; } = new List<string>();

        public Dictionary<string, GCNode> Children { get; set; } = new Dictionary<string, GCNode>(StringComparer.OrdinalIgnoreCase);
        public List<string> ChildOrder { get; set; } = new List<string>();
        public List<GCNode> OrderedChildren { get; set; } = new List<GCNode>();

        public List<GCNode> AnonymousChildren { get; set; } = new List<GCNode>();

        public string SourceFile { get; set; }
        public string CanonicalPath { get; set; }
        public int PackageEntryId { get; set; }

        public IEnumerable<GCNode> EnumerateChildrenInOrder()
        {
            var emittedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var emittedAnonymous = new HashSet<GCNode>();

            if (OrderedChildren != null)
            {
                foreach (GCNode orderedChild in OrderedChildren)
                {
                    if (orderedChild == null)
                        continue;
                    if (orderedChild.IsAnonymous)
                    {
                        if (emittedAnonymous.Add(orderedChild))
                            yield return orderedChild;
                        continue;
                    }

                    string childName = orderedChild.Name ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(childName) || !emittedNames.Add(childName))
                        continue;
                    if (Children != null && Children.TryGetValue(childName, out GCNode effectiveChild) && effectiveChild != null)
                        yield return effectiveChild;
                    else
                        yield return orderedChild;
                }
            }

            if (ChildOrder != null && Children != null)
            {
                foreach (string childName in ChildOrder)
                    if (!string.IsNullOrWhiteSpace(childName)
                        && emittedNames.Add(childName)
                        && Children.TryGetValue(childName, out GCNode child)
                        && child != null)
                        yield return child;
            }

            if (AnonymousChildren != null)
            {
                foreach (GCNode anonymousChild in AnonymousChildren)
                    if (anonymousChild != null && emittedAnonymous.Add(anonymousChild))
                        yield return anonymousChild;
            }
        }

        public void SetProperty(string propertyName, string value)
        {
            if (string.IsNullOrWhiteSpace(propertyName))
                return;
            if (!Properties.ContainsKey(propertyName))
                PropertyOrder.Add(propertyName);
            Properties[propertyName] = value;
        }

        public IEnumerable<KeyValuePair<string, string>> EnumeratePropertiesInOrder()
        {
            var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (PropertyOrder != null)
            {
                foreach (string propertyName in PropertyOrder)
                    if (!string.IsNullOrWhiteSpace(propertyName)
                        && emitted.Add(propertyName)
                        && Properties.TryGetValue(propertyName, out string value))
                        yield return new KeyValuePair<string, string>(propertyName, value);
            }
            foreach (KeyValuePair<string, string> propertyEntry in Properties)
                if (emitted.Add(propertyEntry.Key))
                    yield return propertyEntry;
        }

        public static GCNode MergeInherited(GCNode parent, GCNode child)
        {
            if (parent == null)
                return child;
            if (child == null)
                return parent;

            var merged = new GCNode
            {
                Name = child.Name,
                Extends = child.Extends ?? parent.Extends,
                IsStatic = child.IsStatic || parent.IsStatic,
                IsAnonymous = child.IsAnonymous || parent.IsAnonymous,
                SourceFile = child.SourceFile,
                CanonicalPath = child.CanonicalPath,
                PackageEntryId = child.PackageEntryId
            };

            foreach (KeyValuePair<string, string> propertyEntry in parent.EnumeratePropertiesInOrder())
                merged.SetProperty(propertyEntry.Key, propertyEntry.Value);
            foreach (KeyValuePair<string, string> propertyEntry in child.EnumeratePropertiesInOrder())
                merged.SetProperty(propertyEntry.Key, propertyEntry.Value);

            foreach (GCNode parentChild in parent.EnumerateChildrenInOrder())
            {
                merged.OrderedChildren.Add(parentChild);
                if (parentChild.IsAnonymous)
                {
                    merged.AnonymousChildren.Add(parentChild);
                    continue;
                }
                string childName = parentChild.Name ?? string.Empty;
                if (string.IsNullOrWhiteSpace(childName))
                    continue;
                bool firstNamedChild = !merged.Children.ContainsKey(childName);
                merged.Children[childName] = parentChild;
                if (firstNamedChild)
                    merged.ChildOrder.Add(childName);
            }

            foreach (GCNode incomingChild in child.EnumerateChildrenInOrder())
            {
                merged.OrderedChildren.Add(incomingChild);
                if (incomingChild.IsAnonymous)
                {
                    merged.AnonymousChildren.Add(incomingChild);
                    continue;
                }
                string childName = incomingChild.Name ?? string.Empty;
                if (string.IsNullOrWhiteSpace(childName))
                    continue;
                bool firstNamedChild = !merged.Children.TryGetValue(childName, out GCNode existingChild);
                merged.Children[childName] = !firstNamedChild
                    ? MergeInherited(existingChild, incomingChild)
                    : incomingChild;
                if (firstNamedChild)
                    merged.ChildOrder.Add(childName);
            }

            return merged;
        }


        public string GetString(string key, string fallback = "")
        {
            return Properties.TryGetValue(key, out string val) ? val : fallback;
        }

        public float GetFloat(string key, float fallback = 0f)
        {
            if (Properties.TryGetValue(key, out string val))
            {
                if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float result))
                    return result;
            }
            return fallback;
        }

        public int GetFixed32(string key, int fallbackF32 = 0)
        {
            return Properties.TryGetValue(key, out string val) && TryParseFixed32Core(val, out int result)
                ? result
                : fallbackF32;
        }

        public int GetFixed32Ceiling(string key, int fallbackF32 = 0)
        {
            return Properties.TryGetValue(key, out string val) && TryParseFixed32Core(val, out int result)
                ? result
                : fallbackF32;
        }

        public static bool TryParseFixed32(string text, out int result)
        {
            return TryParseFixed32Core(text, out result);
        }

        private static bool TryParseFixed32Core(string text, out int result)
        {
            result = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string value = text.Trim();
            int index = 0;
            bool negative = false;
            if (value[index] == '+' || value[index] == '-')
            {
                negative = value[index] == '-';
                index++;
            }
            if (index >= value.Length) return false;

            BigInteger digits = BigInteger.Zero;
            int fractionalDigits = 0;
            bool afterDecimal = false;
            bool sawDigit = false;
            while (index < value.Length)
            {
                char ch = value[index];
                if (ch >= '0' && ch <= '9')
                {
                    digits = digits * 10 + (ch - '0');
                    if (afterDecimal) fractionalDigits++;
                    sawDigit = true;
                    index++;
                    continue;
                }
                if (ch == '.' && !afterDecimal)
                {
                    afterDecimal = true;
                    index++;
                    continue;
                }
                break;
            }
            if (!sawDigit) return false;

            int exponent = 0;
            if (index < value.Length && (value[index] == 'e' || value[index] == 'E'))
            {
                index++;
                bool exponentNegative = false;
                if (index < value.Length && (value[index] == '+' || value[index] == '-'))
                {
                    exponentNegative = value[index] == '-';
                    index++;
                }
                if (index >= value.Length || value[index] < '0' || value[index] > '9') return false;
                while (index < value.Length && value[index] >= '0' && value[index] <= '9')
                {
                    if (exponent > 1000) return false;
                    exponent = exponent * 10 + (value[index] - '0');
                    index++;
                }
                if (exponentNegative) exponent = -exponent;
            }
            if (index != value.Length) return false;

            int decimalScale = fractionalDigits - exponent;
            BigInteger scaled = digits * 256;
            BigInteger rounded;
            if (decimalScale > 0)
            {
                if (decimalScale > 1000) return false;
                BigInteger divisor = BigInteger.Pow(10, decimalScale);
                BigInteger quotient = BigInteger.DivRem(scaled, divisor, out BigInteger remainder);
                if (!negative && !remainder.IsZero)
                    quotient += 1;
                rounded = quotient;
            }
            else
            {
                if (decimalScale < -1000) return false;
                rounded = scaled * BigInteger.Pow(10, -decimalScale);
            }

            if (negative) rounded = -rounded;
            if (rounded > int.MaxValue || rounded < int.MinValue) return false;
            result = (int)rounded;
            return true;
        }

        public int GetInt(string key, int fallback = 0)
        {
            if (Properties.TryGetValue(key, out string val))
            {
                if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result))
                    return result;
                if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                    return (int)f;
            }
            return fallback;
        }

        public bool GetBool(string key, bool fallback = false)
        {
            if (Properties.TryGetValue(key, out string val))
            {
                return val.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                       val.Equals("1", StringComparison.Ordinal);
            }
            return fallback;
        }

        public bool HasProperty(string key) => Properties.ContainsKey(key);
        public bool HasChild(string name) => Children.ContainsKey(name);
        public GCNode GetChild(string name) => Children.TryGetValue(name, out GCNode c) ? c : null;

        public GCNode ResolvePath(string dottedPath)
        {
            if (string.IsNullOrEmpty(dottedPath)) return this;

            string[] parts = dottedPath.Split('.');
            GCNode current = this;

            foreach (string part in parts)
            {
                if (current.Children.TryGetValue(part, out GCNode child))
                {
                    current = child;
                }
                else
                {
                    return null;
                }
            }
            return current;
        }

        public string GetNestedProperty(string childName, string propName)
        {
            var child = GetChild(childName);
            if (child != null && child.Properties.TryGetValue(propName, out string val))
                return val;
            return null;
        }

        public float GetNestedFloat(string childName, string propName, float fallback = 0f)
        {
            string val = GetNestedProperty(childName, propName);
            if (val != null && float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float result))
                return result;
            return fallback;
        }

        public int GetNestedInt(string childName, string propName, int fallback = 0)
        {
            string val = GetNestedProperty(childName, propName);
            if (val != null)
            {
                if (int.TryParse(val, out int result)) return result;
                if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)) return (int)f;
            }
            return fallback;
        }

        public override string ToString()
        {
            return $"GCNode[{Name}] extends={Extends ?? "none"} props={Properties.Count} children={Children.Count}";
        }
    }

    public static class GcParser
    {
        public static GCNode ParseFile(string filePath)
        {
            string text = File.ReadAllText(filePath);
            string fileName = Path.GetFileNameWithoutExtension(filePath);
            return Parse(text, fileName);
        }

        public static GCNode Parse(string text, string sourceFile = "")
        {
            text = StripComments(text);
            int pos = 0;
            var node = ParseTopLevel(text, ref pos, sourceFile);
            return node;
        }


        private static string StripComments(string text)
        {
            var sb = new StringBuilder(text.Length);
            int i = 0;
            bool inString = false;

            while (i < text.Length)
            {
                if (text[i] == '"' && (i == 0 || text[i - 1] != '\\'))
                {
                    inString = !inString;
                    sb.Append(text[i]);
                    i++;
                    continue;
                }

                if (inString)
                {
                    sb.Append(text[i]);
                    i++;
                    continue;
                }

                if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '/')
                {
                    while (i < text.Length && text[i] != '\n') i++;
                    continue;
                }

                if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/')) i++;
                    if (i + 1 < text.Length) i += 2;
                    continue;
                }

                sb.Append(text[i]);
                i++;
            }

            return sb.ToString();
        }


        private static GCNode ParseTopLevel(string text, ref int pos, string sourceFile)
        {
            SkipWhitespace(text, ref pos);

            string name = null;
            string extends_ = null;
            bool isStatic = false;

            if (TryReadWord(text, ref pos, out string firstWord))
            {
                if (firstWord.Equals("static", StringComparison.OrdinalIgnoreCase))
                {
                    isStatic = true;
                    SkipWhitespace(text, ref pos);
                    TryReadWord(text, ref pos, out name);
                }
                else
                {
                    name = firstWord;
                }
            }

            if (name == null) return null;

            SkipWhitespace(text, ref pos);

            int savedPos = pos;
            if (TryReadWord(text, ref pos, out string maybeExtends))
            {
                if (maybeExtends.Equals("extends", StringComparison.OrdinalIgnoreCase))
                {
                    SkipWhitespace(text, ref pos);
                    TryReadWord(text, ref pos, out extends_);
                }
                else
                {
                    pos = savedPos;
                }
            }

            SkipWhitespace(text, ref pos);

            var node = new GCNode
            {
                Name = name,
                Extends = extends_,
                IsStatic = isStatic,
                IsAnonymous = name == "*",
                SourceFile = sourceFile
            };

            if (pos < text.Length && text[pos] == '{')
            {
                pos++;
                ParseBlockBody(text, ref pos, node, sourceFile);
            }

            return node;
        }


        private static void ParseBlockBody(string text, ref int pos, GCNode parent, string sourceFile)
        {
            while (pos < text.Length)
            {
                SkipWhitespace(text, ref pos);
                if (pos >= text.Length) break;

                if (text[pos] == '}')
                {
                    pos++;
                    return;
                }

                bool isStatic = false;
                if (!TryReadWord(text, ref pos, out string word)) break;

                if (word.Equals("static", StringComparison.OrdinalIgnoreCase))
                {
                    isStatic = true;
                    SkipWhitespace(text, ref pos);
                    if (!TryReadWord(text, ref pos, out word)) break;
                }

                SkipWhitespace(text, ref pos);
                if (pos >= text.Length) break;

                char next = text[pos];

                if (next == '=')
                {
                    pos++;
                    SkipWhitespace(text, ref pos);
                    string value = ReadValue(text, ref pos);
                    parent.SetProperty(word, value);
                }
                else if (next == '{' || IsWord(text, pos, "extends"))
                {
                    string childExtends = null;

                    if (IsWord(text, pos, "extends"))
                    {
                        pos += 7;
                        SkipWhitespace(text, ref pos);
                        TryReadWord(text, ref pos, out childExtends);
                        SkipWhitespace(text, ref pos);
                    }

                    var child = new GCNode
                    {
                        Name = word,
                        Extends = childExtends,
                        IsStatic = isStatic,
                        IsAnonymous = word == "*",
                        SourceFile = sourceFile
                    };

                    if (pos < text.Length && text[pos] == '{')
                    {
                        pos++;
                        ParseBlockBody(text, ref pos, child, sourceFile);
                    }

                    parent.OrderedChildren.Add(child);
                    if (child.IsAnonymous)
                        parent.AnonymousChildren.Add(child);
                    else
                    {
                        parent.Children[word] = child;
                        if (!parent.ChildOrder.Contains(word))
                            parent.ChildOrder.Add(word);
                    }
                }
                else
                {
                    SkipToNextStatement(text, ref pos);
                }
            }
        }


        private static string ReadValue(string text, ref int pos)
        {
            SkipWhitespace(text, ref pos);
            if (pos >= text.Length) return "";

            var sb = new StringBuilder();

            if (text[pos] == '"')
            {
                pos++;
                while (pos < text.Length && text[pos] != '"')
                {
                    if (text[pos] == '\\' && pos + 1 < text.Length)
                    {
                        sb.Append(text[pos + 1]);
                        pos += 2;
                    }
                    else
                    {
                        sb.Append(text[pos]);
                        pos++;
                    }
                }
                if (pos < text.Length) pos++;
            }
            else
            {
                while (pos < text.Length && text[pos] != ';' && text[pos] != '\n' && text[pos] != '\r' && text[pos] != '}')
                {
                    sb.Append(text[pos]);
                    pos++;
                }
            }

            SkipWhitespace(text, ref pos);
            if (pos < text.Length && text[pos] == ';') pos++;

            return sb.ToString().Trim();
        }


        private static void SkipWhitespace(string text, ref int pos)
        {
            while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++;
        }

        private static bool TryReadWord(string text, ref int pos, out string word)
        {
            SkipWhitespace(text, ref pos);
            if (pos >= text.Length)
            {
                word = null;
                return false;
            }

            int start = pos;
            while (pos < text.Length && (char.IsLetterOrDigit(text[pos]) || text[pos] == '_' || text[pos] == '.' || text[pos] == '/' || text[pos] == '\\' || text[pos] == '-' || text[pos] == '*' || text[pos] == ':'))
            {
                pos++;
            }

            if (pos == start)
            {
                word = null;
                return false;
            }

            word = text.Substring(start, pos - start);
            return true;
        }

        private static bool IsWord(string text, int pos, string word)
        {
            if (pos + word.Length > text.Length) return false;
            for (int wordIndex = 0; wordIndex < word.Length; wordIndex++)
            {
                if (char.ToLower(text[pos + wordIndex]) != char.ToLower(word[wordIndex])) return false;
            }
            if (pos + word.Length < text.Length && char.IsLetterOrDigit(text[pos + word.Length])) return false;
            return true;
        }

        private static void SkipToNextStatement(string text, ref int pos)
        {
            while (pos < text.Length && text[pos] != ';' && text[pos] != '}' && text[pos] != '{' && text[pos] != '\n')
                pos++;
            if (pos < text.Length && text[pos] == ';') pos++;
        }
    }
}
