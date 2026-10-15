using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SLNG.Core;

public static class LlsdNotation
{
    public const int MaxBytes = 16 * 1024;
    public const int MaxDepth = 32;

    public static bool TryParse(string text, out LlsdValue value, out string error)
    {
        if (text == null)
        {
            value = LlsdValue.Undefined;
            error = "input is null";
            return false;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(text);
        return TryParse(bytes, out value, out error);
    }

    public static bool TryParse(ReadOnlySpan<byte> utf8, out LlsdValue value, out string error)
    {
        value = LlsdValue.Undefined;
        error = string.Empty;

        if (utf8.Length > MaxBytes)
        {
            error = "input exceeds maximum allowed bytes";
            return false;
        }

        // Strip UTF-8 BOM if present
        if (utf8.Length >= 3 && utf8[0] == 0xEF && utf8[1] == 0xBB && utf8[2] == 0xBF)
        {
            utf8 = utf8[3..];
        }

        string text;
        try
        {
            text = Encoding.UTF8.GetString(utf8);
        }
        catch (Exception ex)
        {
            error = $"UTF-8 decode failed: {ex.Message}";
            return false;
        }

        var parser = new Parser(text);
        return parser.TryParse(out value, out error);
    }

    private sealed class Parser
    {
        private readonly string _s;
        private int _i;

        public Parser(string s)
        {
            _s = s;
            _i = 0;
        }

        public bool TryParse(out LlsdValue value, out string error)
        {
            value = LlsdValue.Undefined;
            error = string.Empty;
            try
            {
                SkipWhiteAndNull();
                if (_i >= _s.Length)
                {
                    error = "empty input";
                    return false;
                }

                value = ParseValue(0);
                return true;
            }
            catch (Exception ex)
            {
                value = LlsdValue.Undefined;
                error = ex.Message;
                return false;
            }
        }

        private Exception Fail(string msg) => new($"{msg} at offset {_i}");

        private void SkipWhiteAndNull()
        {
            while (_i < _s.Length && (char.IsWhiteSpace(_s[_i]) || _s[_i] == '\0'))
            {
                _i++;
            }
        }

        private void SkipWhite()
        {
            while (_i < _s.Length && char.IsWhiteSpace(_s[_i]))
            {
                _i++;
            }
        }

        private void SkipWhiteAndCommas()
        {
            while (_i < _s.Length && (char.IsWhiteSpace(_s[_i]) || _s[_i] == ',' || _s[_i] == '\0'))
            {
                _i++;
            }
        }

        private LlsdValue ParseValue(int depth)
        {
            if (depth > MaxDepth)
            {
                throw Fail("depth exceeds maximum allowed depth");
            }

            SkipWhiteAndNull();
            if (_i >= _s.Length)
            {
                throw Fail("unexpected end of input");
            }

            char c = _s[_i];
            if (c == '!')
            {
                _i++;
                return LlsdValue.Undefined;
            }
            if (c == '{')
            {
                _i++;
                return ParseMap(depth);
            }
            if (c == '[')
            {
                _i++;
                return ParseArray(depth);
            }
            if (c == '0')
            {
                _i++;
                return LlsdValue.FromBoolean(false);
            }
            if (c == '1')
            {
                _i++;
                return LlsdValue.FromBoolean(true);
            }
            if (c == 't' || c == 'T')
            {
                return ParseBooleanTrue();
            }
            if (c == 'f' || c == 'F')
            {
                return ParseBooleanFalse();
            }
            if (c == 'i')
            {
                _i++;
                return ParseInteger();
            }
            if (c == 'r')
            {
                _i++;
                return ParseReal();
            }
            if (c == 'u')
            {
                _i++;
                return ParseUuid();
            }
            if (c == '\'' || c == '"')
            {
                return LlsdValue.FromString(ParseQuotedString());
            }
            if (c == 's')
            {
                return LlsdValue.FromString(ParseLengthPrefixedString());
            }
            if (c == 'l' || c == 'd' || c == 'b')
            {
                return ParseOther();
            }

            throw Fail($"unknown type marker '{c}'");
        }

        private LlsdValue ParseMap(int depth)
        {
            var map = new Dictionary<string, LlsdValue>();
            while (true)
            {
                SkipWhiteAndCommas();
                if (_i >= _s.Length) throw Fail("unterminated map");
                if (_s[_i] == '}')
                {
                    _i++;
                    return LlsdValue.FromMap(map);
                }

                char c = _s[_i];
                if (c != '\'' && c != '"' && c != 's')
                {
                    throw Fail("map key is not a string");
                }

                string key = c == 's' ? ParseLengthPrefixedString() : ParseQuotedString();
                SkipWhite();
                if (_i >= _s.Length || _s[_i] != ':')
                {
                    throw Fail("expected ':' after a map key");
                }
                _i++; // consume ':'
                SkipWhite();

                var val = ParseValue(depth + 1);
                map[key] = val;
            }
        }

        private LlsdValue ParseArray(int depth)
        {
            var list = new List<LlsdValue>();
            while (true)
            {
                SkipWhiteAndCommas();
                if (_i >= _s.Length) throw Fail("unterminated array");
                if (_s[_i] == ']')
                {
                    _i++;
                    return LlsdValue.FromArray(list);
                }

                var val = ParseValue(depth + 1);
                list.Add(val);
            }
        }

        private LlsdValue ParseBooleanTrue()
        {
            if (MatchesWord("true"))
            {
                _i += 4;
                return LlsdValue.FromBoolean(true);
            }
            if (MatchesWord("TRUE"))
            {
                _i += 4;
                return LlsdValue.FromBoolean(true);
            }
            char c = _s[_i];
            if (c == 't' || c == 'T')
            {
                _i++;
                return LlsdValue.FromBoolean(true);
            }
            throw Fail($"unknown boolean true marker '{c}'");
        }

        private LlsdValue ParseBooleanFalse()
        {
            if (MatchesWord("false"))
            {
                _i += 5;
                return LlsdValue.FromBoolean(false);
            }
            if (MatchesWord("FALSE"))
            {
                _i += 5;
                return LlsdValue.FromBoolean(false);
            }
            char c = _s[_i];
            if (c == 'f' || c == 'F')
            {
                _i++;
                return LlsdValue.FromBoolean(false);
            }
            throw Fail($"unknown boolean false marker '{c}'");
        }

        private bool MatchesWord(string word)
        {
            if (_i + word.Length > _s.Length) return false;
            for (int k = 0; k < word.Length; k++)
            {
                if (_s[_i + k] != word[k]) return false;
            }
            // Check word boundary: next char shouldn't be letter or digit
            if (_i + word.Length < _s.Length)
            {
                char next = _s[_i + word.Length];
                if (char.IsLetterOrDigit(next)) return false;
            }
            return true;
        }

        private LlsdValue ParseInteger()
        {
            int start = _i;
            if (_i < _s.Length && (_s[_i] == '+' || _s[_i] == '-'))
            {
                _i++;
            }
            int digitStart = _i;
            while (_i < _s.Length && char.IsDigit(_s[_i]))
            {
                _i++;
            }
            if (_i == digitStart)
            {
                throw Fail("expected integer digits");
            }
            string token = _s[start.._i];
            if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int val))
            {
                throw Fail($"invalid or out of range integer '{token}'");
            }
            return LlsdValue.FromInteger(val);
        }

        private LlsdValue ParseReal()
        {
            int start = _i;
            while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '+' || _s[_i] == '-' || _s[_i] == '.'))
            {
                _i++;
            }
            if (_i == start)
            {
                throw Fail("expected real token");
            }
            string token = _s[start.._i];
            string lower = token.ToLowerInvariant();
            if (lower == "nan")
            {
                return LlsdValue.FromReal(double.NaN);
            }
            if (lower == "inf" || lower == "+inf")
            {
                return LlsdValue.FromReal(double.PositiveInfinity);
            }
            if (lower == "-inf")
            {
                return LlsdValue.FromReal(double.NegativeInfinity);
            }
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
            {
                throw Fail($"invalid real '{token}'");
            }
            return LlsdValue.FromReal(val);
        }

        private LlsdValue ParseUuid()
        {
            if (_i + 36 > _s.Length)
            {
                throw Fail("uuid string too short");
            }
            string guidSpan = _s.Substring(_i, 36);
            if (!Guid.TryParse(guidSpan, out Guid guid))
            {
                throw Fail($"invalid uuid '{guidSpan}'");
            }
            _i += 36;
            return LlsdValue.FromUuid(guid);
        }

        private string ParseQuotedString()
        {
            char quote = _s[_i++];
            var sb = new StringBuilder();
            while (_i < _s.Length)
            {
                char c = _s[_i++];
                if (c == quote)
                {
                    return sb.ToString();
                }
                if (c == '\\')
                {
                    if (_i >= _s.Length) throw Fail("unterminated string escape");
                    char esc = _s[_i++];
                    switch (esc)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case '\\': sb.Append('\\'); break;
                        case '\'': sb.Append('\''); break;
                        case '"': sb.Append('"'); break;
                        case 'x':
                            if (_i + 2 > _s.Length) throw Fail("hex escape too short");
                            string hex = _s.Substring(_i, 2);
                            _i += 2;
                            if (byte.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
                            {
                                sb.Append((char)b);
                            }
                            else
                            {
                                throw Fail($"invalid hex escape \\x{hex}");
                            }
                            break;
                        default:
                            sb.Append(esc);
                            break;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            throw Fail("unterminated string");
        }

        private string ParseLengthPrefixedString()
        {
            _i++; // consume 's'
            if (_i >= _s.Length || _s[_i] != '(') throw Fail("expected '(' in s(len)\"...\"");
            _i++; // consume '('
            int lenStart = _i;
            while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
            if (_i == lenStart || _i >= _s.Length || _s[_i] != ')') throw Fail("invalid s(len) length");
            string lenStr = _s[lenStart.._i];
            _i++; // consume ')'
            if (!int.TryParse(lenStr, out int count) || count < 0) throw Fail("invalid string length count");
            SkipWhite();
            if (_i >= _s.Length || _s[_i] != '"') throw Fail("expected '\"' after s(count)");
            _i++; // consume '"'
            if (_i + count > _s.Length) throw Fail("s(count) string longer than input");
            string result = _s.Substring(_i, count);
            _i += count;
            if (_i >= _s.Length || _s[_i] != '"') throw Fail("expected closing '\"' for s(count)\"...\"");
            _i++; // consume closing '"'
            return result;
        }

        private LlsdValue ParseOther()
        {
            char marker = _s[_i++];
            if (marker == 'l' || marker == 'd')
            {
                // URI or Date: typically l"..." or d"..."
                SkipWhite();
                if (_i < _s.Length && (_s[_i] == '"' || _s[_i] == '\''))
                {
                    ParseQuotedString();
                }
            }
            else if (marker == 'b')
            {
                // Binary: b64"..." or b(n)"..."
                SkipWhite();
                if (_i < _s.Length && _s[_i] == '(')
                {
                    // b(n)"..."
                    while (_i < _s.Length && _s[_i] != ')') _i++;
                    if (_i < _s.Length) _i++; // ')'
                    SkipWhite();
                    if (_i < _s.Length && (_s[_i] == '"' || _s[_i] == '\'')) ParseQuotedString();
                }
                else
                {
                    while (_i < _s.Length && !char.IsWhiteSpace(_s[_i]) && _s[_i] != ',' && _s[_i] != ']' && _s[_i] != '}')
                    {
                        if (_s[_i] == '"' || _s[_i] == '\'')
                        {
                            ParseQuotedString();
                            break;
                        }
                        _i++;
                    }
                }
            }
            return LlsdValue.Other();
        }
    }
}
