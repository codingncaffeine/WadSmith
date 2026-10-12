using System.Globalization;
using System.Text;

namespace WadSmith.Core.Maps.Udmf;

/// <summary>Reads and writes TEXTMAP lumps.</summary>
public static class UdmfText
{
    public static UdmfDocument Parse(byte[] textmap) => Parse(Encoding.UTF8.GetString(textmap));

    /// <summary>
    /// Parses UDMF text: assignments and blocks, <c>//</c> and <c>/* */</c> comments, decimal, octal
    /// (leading zero) and hexadecimal integers, floats, quoted strings with backslash escapes, and the
    /// keywords <c>true</c> and <c>false</c>.
    /// </summary>
    public static UdmfDocument Parse(string text) => new Parser(text).Run();

    public static byte[] Write(UdmfDocument doc) => Encoding.UTF8.GetBytes(WriteString(doc));

    public static string WriteString(UdmfDocument doc)
    {
        var sb = new StringBuilder(64 * 1024);
        foreach (var (name, value) in doc.Globals) sb.Append(name).Append(" = ").Append(value.ToUdmf()).Append(";\n");
        sb.Append('\n');

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in doc.Blocks)
        {
            counts.TryGetValue(block.Type, out var n);
            counts[block.Type] = n + 1;
            sb.Append(block.Type).Append(" // ").Append(n.ToString(CultureInfo.InvariantCulture)).Append("\n{\n");
            foreach (var (name, value) in block.Fields) sb.Append(name).Append(" = ").Append(value.ToUdmf()).Append(";\n");
            sb.Append("}\n\n");
        }

        return sb.ToString();
    }

    private sealed class Parser(string s)
    {
        private int _pos;
        private int _line = 1;

        public UdmfDocument Run()
        {
            var doc = new UdmfDocument();
            while (true)
            {
                SkipSpace();
                if (_pos >= s.Length) return doc;
                var name = Identifier();
                SkipSpace();
                if (Peek() == '{')
                {
                    _pos++;
                    var block = new UdmfBlock(name.ToLowerInvariant());
                    while (true)
                    {
                        SkipSpace();
                        if (Peek() == '}')
                        {
                            _pos++;
                            break;
                        }

                        if (_pos >= s.Length) throw Error($"the \"{name}\" block is never closed with }}.");
                        var field = Identifier();
                        Assignment(field, block.Fields);
                    }

                    doc.Blocks.Add(block);
                }
                else
                {
                    Assignment(name, doc.Globals);
                }
            }
        }

        private void Assignment(string field, UdmfFields into)
        {
            SkipSpace();
            if (Peek() != '=') throw Error($"expected = after \"{field}\".");
            _pos++;
            SkipSpace();
            var value = Value(field);
            SkipSpace();
            if (Peek() != ';') throw Error($"expected ; after the value of \"{field}\".");
            _pos++;
            into[field] = value;
        }

        private UdmfValue Value(string field)
        {
            var c = Peek();
            if (c == '"') return UdmfValue.Str(QuotedString());
            var start = _pos;
            while (_pos < s.Length && !char.IsWhiteSpace(s[_pos]) && s[_pos] is not (';' or '{' or '}' or '"' or '=')) _pos++;
            var token = s[start.._pos];
            if (token.Length == 0) throw Error($"\"{field}\" has no value.");
            if (token.Equals("true", StringComparison.OrdinalIgnoreCase)) return UdmfValue.Bool(true);
            if (token.Equals("false", StringComparison.OrdinalIgnoreCase)) return UdmfValue.Bool(false);
            if (TryInteger(token, out var integer)) return UdmfValue.Int(integer);
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return UdmfValue.Float(number);
            throw Error($"\"{token}\" is not a number, a quoted string, true or false (the value of \"{field}\").");
        }

        private static bool TryInteger(string token, out long value)
        {
            value = 0;
            var span = token.AsSpan();
            var negative = false;
            if (span.Length > 0 && span[0] is '+' or '-')
            {
                negative = span[0] == '-';
                span = span[1..];
            }

            if (span.Length == 0) return false;
            bool ok;
            if (span.Length > 2 && span[0] == '0' && span[1] is 'x' or 'X')
            {
                ok = long.TryParse(span[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
            }
            else if (span.Length > 1 && span[0] == '0')
            {
                ok = true;
                foreach (var d in span)
                {
                    if (d is < '0' or > '7')
                    {
                        ok = false;
                        break;
                    }

                    value = value * 8 + (d - '0');
                }
            }
            else
            {
                foreach (var d in span)
                {
                    if (!char.IsAsciiDigit(d)) return false;
                }

                ok = long.TryParse(span, NumberStyles.None, CultureInfo.InvariantCulture, out value);
            }

            if (ok && negative) value = -value;
            return ok;
        }

        private string QuotedString()
        {
            var startLine = _line;
            _pos++;
            var sb = new StringBuilder();
            while (_pos < s.Length)
            {
                var c = s[_pos++];
                if (c == '"') return sb.ToString();
                if (c == '\\' && _pos < s.Length)
                {
                    c = s[_pos++];
                }

                if (c == '\n') _line++;
                sb.Append(c);
            }

            _line = startLine;
            throw Error("a quoted string is never closed.");
        }

        private string Identifier()
        {
            var start = _pos;
            if (_pos < s.Length && (char.IsAsciiLetter(s[_pos]) || s[_pos] == '_'))
            {
                _pos++;
                while (_pos < s.Length && (char.IsAsciiLetterOrDigit(s[_pos]) || s[_pos] == '_')) _pos++;
            }

            if (_pos == start) throw Error(_pos < s.Length ? $"unexpected \"{s[_pos]}\"." : "the text ends too early.");
            return s[start.._pos];
        }

        private char Peek() => _pos < s.Length ? s[_pos] : '\0';

        private void SkipSpace()
        {
            while (_pos < s.Length)
            {
                var c = s[_pos];
                if (c == '\n')
                {
                    _line++;
                    _pos++;
                }
                else if (char.IsWhiteSpace(c))
                {
                    _pos++;
                }
                else if (c == '/' && _pos + 1 < s.Length && s[_pos + 1] == '/')
                {
                    while (_pos < s.Length && s[_pos] != '\n') _pos++;
                }
                else if (c == '/' && _pos + 1 < s.Length && s[_pos + 1] == '*')
                {
                    var end = s.IndexOf("*/", _pos + 2, StringComparison.Ordinal);
                    if (end < 0) throw Error("a /* comment is never closed.");
                    for (var i = _pos; i < end; i++)
                    {
                        if (s[i] == '\n') _line++;
                    }

                    _pos = end + 2;
                }
                else
                {
                    return;
                }
            }
        }

        private CorruptDataException Error(string what) => new($"TEXTMAP line {_line.ToString(CultureInfo.InvariantCulture)}: {what}");
    }
}
