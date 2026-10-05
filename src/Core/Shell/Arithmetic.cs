using System;

namespace Zenith.Core.Shell;

/// <summary>
/// Shell arithmetic, <c>$(( ... ))</c>: 64-bit integers with the C operators sh supports most
/// often, by precedence: <c>|| &amp;&amp;</c>, <c>== !=</c>, <c>&lt; &lt;= &gt; &gt;=</c>,
/// <c>+ -</c>, <c>* / %</c>, unary <c>- + !</c>, <c>**</c> (power, right-associative), and parentheses. Bare names and <c>$name</c>
/// read variables (empty or non-numeric counts as 0). Comparisons yield 1 or 0.
/// </summary>
internal static class Arithmetic
{
    public static long Evaluate(string expression, Func<string, string> lookupVariable)
    {
        var parser = new State(expression, lookupVariable);
        long value = parser.Or();
        parser.SkipSpaces();
        if (!parser.AtEnd)
        {
            throw new FormatException("arithmetic: unexpected '" + parser.Rest + "'");
        }

        return value;
    }

    private sealed class State
    {
        private readonly string _text;
        private readonly Func<string, string> _lookup;
        private int _pos;

        public State(string text, Func<string, string> lookup)
        {
            _text = text;
            _lookup = lookup;
        }

        public bool AtEnd => _pos >= _text.Length;
        public string Rest => _text.Substring(_pos);

        public void SkipSpaces()
        {
            while (!AtEnd && char.IsWhiteSpace(_text[_pos]))
            {
                _pos++;
            }
        }

        private bool Take(string op)
        {
            SkipSpaces();
            if (string.CompareOrdinal(_text, _pos, op, 0, op.Length) == 0)
            {
                _pos += op.Length;
                return true;
            }

            return false;
        }

        public long Or()
        {
            long left = And();
            while (Take("||"))
            {
                long right = And();
                left = left != 0 || right != 0 ? 1 : 0;
            }

            return left;
        }

        private long And()
        {
            long left = Equality();
            while (Take("&&"))
            {
                long right = Equality();
                left = left != 0 && right != 0 ? 1 : 0;
            }

            return left;
        }

        private long Equality()
        {
            long left = Relational();
            while (true)
            {
                if (Take("=="))
                {
                    left = left == Relational() ? 1 : 0;
                }
                else if (Take("!="))
                {
                    left = left != Relational() ? 1 : 0;
                }
                else
                {
                    return left;
                }
            }
        }

        private long Relational()
        {
            long left = Additive();
            while (true)
            {
                if (Take("<="))
                {
                    left = left <= Additive() ? 1 : 0;
                }
                else if (Take(">="))
                {
                    left = left >= Additive() ? 1 : 0;
                }
                else if (Take("<"))
                {
                    left = left < Additive() ? 1 : 0;
                }
                else if (Take(">"))
                {
                    left = left > Additive() ? 1 : 0;
                }
                else
                {
                    return left;
                }
            }
        }

        private long Additive()
        {
            long left = Multiplicative();
            while (true)
            {
                if (Take("+"))
                {
                    left += Multiplicative();
                }
                else if (Take("-"))
                {
                    left -= Multiplicative();
                }
                else
                {
                    return left;
                }
            }
        }

        private long Multiplicative()
        {
            long left = Power();
            while (true)
            {
                if (Take("**"))
                {
                    throw new FormatException("arithmetic: unexpected '**'");
                }

                if (Take("*"))
                {
                    left *= Power();
                }
                else if (Take("/"))
                {
                    long right = Power();
                    left = right == 0 ? throw new FormatException("arithmetic: division by zero") : left / right;
                }
                else if (Take("%"))
                {
                    long right = Power();
                    left = right == 0 ? throw new FormatException("arithmetic: division by zero") : left % right;
                }
                else
                {
                    return left;
                }
            }
        }

        private long Unary()
        {
            if (Take("-"))
            {
                return -Unary();
            }

            if (Take("+"))
            {
                return Unary();
            }

            if (Take("!"))
            {
                return Unary() == 0 ? 1 : 0;
            }

            return Primary();
        }

        /// <summary>
        /// <c>**</c> binds looser than unary minus (as in bash, <c>-2**2</c> is 4) and tighter than
        /// <c>*</c>; it is right-associative (<c>2**3**2</c> is <c>2**9</c>).
        /// </summary>
        private long Power()
        {
            long value = Unary();
            if (!Take("**"))
            {
                return value;
            }

            long exponent = Power();
            if (exponent < 0)
            {
                throw new FormatException("arithmetic: negative exponent");
            }

            long result = 1;
            for (long i = 0; i < exponent; i++)
            {
                result *= value;
            }

            return result;
        }

        private long Primary()
        {
            SkipSpaces();
            if (Take("("))
            {
                long value = Or();
                if (!Take(")"))
                {
                    throw new FormatException("arithmetic: missing ')'");
                }

                return value;
            }

            int start = _pos;
            if (!AtEnd && char.IsDigit(_text[_pos]))
            {
                while (!AtEnd && char.IsDigit(_text[_pos]))
                {
                    _pos++;
                }

                return long.Parse(_text.Substring(start, _pos - start), System.Globalization.CultureInfo.InvariantCulture);
            }

            if (!AtEnd && _text[_pos] == '$')
            {
                _pos++;
                start = _pos;
            }

            while (!AtEnd && (char.IsLetterOrDigit(_text[_pos]) || _text[_pos] == '_'))
            {
                _pos++;
            }

            if (_pos == start)
            {
                throw new FormatException("arithmetic: unexpected '" + Rest + "'");
            }

            string value2 = _lookup(_text.Substring(start, _pos - start)).Trim();
            return long.TryParse(value2, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out long number) ? number : 0;
        }
    }
}
