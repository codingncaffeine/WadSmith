using System.Globalization;
using System.Text;

namespace WadSmith.Core.Text;

public enum TokenKind
{
    End,
    Identifier,
    String,
    Integer,
    Float,
    Punctuation,
}

public readonly record struct Token(TokenKind Kind, string Text, int Line)
{
    public bool Is(string text) => Kind is TokenKind.Identifier or TokenKind.Punctuation && string.Equals(Text, text, StringComparison.OrdinalIgnoreCase);

    public long Integer => Kind == TokenKind.Integer ? ParseInteger(Text) : throw new FormatException($"\"{Text}\" is not a whole number.");

    public double Number => Kind switch
    {
        TokenKind.Integer => ParseInteger(Text),
        TokenKind.Float => double.Parse(Text, NumberStyles.Float, CultureInfo.InvariantCulture),
        _ => throw new FormatException($"\"{Text}\" is not a number."),
    };

    private static long ParseInteger(string text)
    {
        var negative = text.StartsWith('-');
        var body = negative || text.StartsWith('+') ? text[1..] : text;
        var value = body.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.Parse(body[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
            : long.Parse(body, NumberStyles.None, CultureInfo.InvariantCulture);
        return negative ? -value : value;
    }

    public override string ToString() => Kind == TokenKind.End ? "the end of the text" : $"\"{Text}\"";
}

/// <summary>
/// A tokenizer for the engine's text lumps (TEXTURES, ANIMDEFS, MAPINFO, the xlat tables, DECORATE
/// and the declarations of ZScript): <c>//</c> and <c>/* */</c> comments, quoted strings with
/// backslash escapes, decimal and hexadecimal numbers, identifiers (letters, digits, <c>_</c>) and
/// single-character punctuation. Errors name the lump and the line.
/// </summary>
public sealed class Scanner
{
    private readonly string _s;
    private readonly string _source;
    private int _pos;
    private int _line = 1;
    private Token? _peeked;

    public Scanner(string text, string source)
    {
        _s = text;
        _source = source;
    }

    public static Scanner FromBytes(byte[] data, string source) => new(Encoding.Latin1.GetString(data), source);

    public string Source => _source;

    /// <summary>Reads 'single-quoted' text as a string token too (ZScript's name literals).</summary>
    public bool AllowNameLiterals { get; init; }

    /// <summary>A <c>;</c> starts a comment to the end of its line, as in the engine's classic Hexen scanner (MAPINFO's).</summary>
    public bool SemicolonComments { get; init; }

    public int Line => _peeked?.Line ?? _line;

    public Token Peek() => _peeked ??= Read();

    public Token Next()
    {
        var t = Peek();
        _peeked = null;
        return t;
    }

    public bool AtEnd => Peek().Kind == TokenKind.End;

    /// <summary>Consumes the next token if it is this word or punctuation.</summary>
    public bool Accept(string text)
    {
        if (!Peek().Is(text)) return false;
        Next();
        return true;
    }

    public void Expect(string text)
    {
        var t = Next();
        if (!t.Is(text)) throw Error($"expected \"{text}\" but found {t}.", t.Line);
    }

    public string ExpectIdentifier()
    {
        var t = Next();
        if (t.Kind != TokenKind.Identifier) throw Error($"expected a name but found {t}.", t.Line);
        return t.Text;
    }

    /// <summary>A quoted string, or a bare identifier where the formats allow one in place of a string.</summary>
    public string ExpectName()
    {
        var t = Next();
        if (t.Kind is not (TokenKind.String or TokenKind.Identifier or TokenKind.Integer)) throw Error($"expected a name but found {t}.", t.Line);
        return t.Text;
    }

    public long ExpectInteger()
    {
        var negative = Accept("-");
        var t = Next();
        if (t.Kind != TokenKind.Integer) throw Error($"expected a whole number but found {t}.", t.Line);
        return negative ? -t.Integer : t.Integer;
    }

    public double ExpectNumber()
    {
        var negative = Accept("-");
        var t = Next();
        if (t.Kind is not (TokenKind.Integer or TokenKind.Float)) throw Error($"expected a number but found {t}.", t.Line);
        return negative ? -t.Number : t.Number;
    }

    /// <summary>Skips a balanced <c>{ … }</c> block whose opening brace has already been read.</summary>
    public void SkipBlock()
    {
        var depth = 1;
        while (depth > 0)
        {
            var t = Next();
            if (t.Kind == TokenKind.End) throw Error("a { block is never closed.", t.Line);
            if (t.Is("{")) depth++;
            else if (t.Is("}")) depth--;
        }
    }

    public CorruptDataException Error(string what, int? line = null) =>
        new($"{_source} line {(line ?? Line).ToString(CultureInfo.InvariantCulture)}: {what}");

    private Token Read()
    {
        SkipSpace();
        if (_pos >= _s.Length) return new Token(TokenKind.End, "", _line);
        var c = _s[_pos];
        var line = _line;

        if (c == '"' || (c == '\'' && AllowNameLiterals))
        {
            var quote = c;
            _pos++;
            var sb = new StringBuilder();
            while (_pos < _s.Length && _s[_pos] != quote)
            {
                var ch = _s[_pos++];
                if (ch == '\\' && _pos < _s.Length)
                {
                    ch = _s[_pos++];
                    ch = ch switch { 'n' => '\n', 't' => '\t', _ => ch };
                }

                if (ch == '\n') _line++;
                sb.Append(ch);
            }

            if (_pos >= _s.Length) throw Error("a quoted string is never closed.", line);
            _pos++;
            return new Token(TokenKind.String, sb.ToString(), line);
        }

        if (char.IsAsciiDigit(c) || (c == '.' && _pos + 1 < _s.Length && char.IsAsciiDigit(_s[_pos + 1])))
        {
            var start = _pos;
            if (c == '0' && _pos + 1 < _s.Length && _s[_pos + 1] is 'x' or 'X')
            {
                _pos += 2;
                while (_pos < _s.Length && char.IsAsciiHexDigit(_s[_pos])) _pos++;
                return new Token(TokenKind.Integer, _s[start.._pos], line);
            }

            var isFloat = false;
            while (_pos < _s.Length && (char.IsAsciiDigit(_s[_pos]) || _s[_pos] == '.'))
            {
                if (_s[_pos] == '.') isFloat = true;
                _pos++;
            }

            if (_pos < _s.Length && _s[_pos] is 'e' or 'E' && _pos + 1 < _s.Length && (char.IsAsciiDigit(_s[_pos + 1]) || _s[_pos + 1] is '+' or '-'))
            {
                isFloat = true;
                _pos += 2;
                while (_pos < _s.Length && char.IsAsciiDigit(_s[_pos])) _pos++;
            }

            // A number running straight into letters is a name (sprite and texture names like 3DTEX01).
            if (_pos < _s.Length && (char.IsAsciiLetter(_s[_pos]) || _s[_pos] == '_'))
            {
                while (_pos < _s.Length && IsNameChar(_s[_pos])) _pos++;
                return new Token(TokenKind.Identifier, _s[start.._pos], line);
            }

            return new Token(isFloat ? TokenKind.Float : TokenKind.Integer, _s[start.._pos], line);
        }

        if (char.IsAsciiLetter(c) || c == '_')
        {
            var start = _pos;
            while (_pos < _s.Length && IsNameChar(_s[_pos])) _pos++;
            return new Token(TokenKind.Identifier, _s[start.._pos], line);
        }

        _pos++;
        return new Token(TokenKind.Punctuation, c.ToString(), line);
    }

    private static bool IsNameChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    private void SkipSpace()
    {
        while (_pos < _s.Length)
        {
            var c = _s[_pos];
            if (c == '\n')
            {
                _line++;
                _pos++;
            }
            else if (char.IsWhiteSpace(c) || c == '\0')
            {
                _pos++;
            }
            else if ((c == '/' && _pos + 1 < _s.Length && _s[_pos + 1] == '/') || (c == ';' && SemicolonComments))
            {
                while (_pos < _s.Length && _s[_pos] != '\n') _pos++;
            }
            else if (c == '/' && _pos + 1 < _s.Length && _s[_pos + 1] == '*')
            {
                var end = _s.IndexOf("*/", _pos + 2, StringComparison.Ordinal);
                var stop = end < 0 ? _s.Length : end + 2;
                for (var i = _pos; i < stop; i++)
                {
                    if (_s[i] == '\n') _line++;
                }

                _pos = stop;
            }
            else
            {
                return;
            }
        }
    }
}
