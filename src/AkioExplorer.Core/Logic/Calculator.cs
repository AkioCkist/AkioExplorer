using System.Globalization;

namespace AkioExplorer.Core.Logic;

/// <summary>
/// Evaluates arithmetic typed into the quick-search box ("2+2*0.231314/2", "sqrt(2)^3", "15% * 80", "5!").
/// Supports + - * / ^ (also ×, ÷, x, **, mod), parentheses, implicit multiplication ("2pi", "3(4+1)"),
/// postfix % and !, the constants pi/π/e/tau and the usual functions (radians for trigonometry).
/// A plain number is not a calculation: something has to be computed, so "2024" stays a file search.
/// </summary>
public static class Calculator
{
    private static readonly Dictionary<string, Func<double, double>> Functions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sqrt"] = Math.Sqrt,
        ["cbrt"] = Math.Cbrt,
        ["abs"] = Math.Abs,
        ["sin"] = Math.Sin,
        ["cos"] = Math.Cos,
        ["tan"] = Math.Tan,
        ["asin"] = Math.Asin,
        ["acos"] = Math.Acos,
        ["atan"] = Math.Atan,
        ["sinh"] = Math.Sinh,
        ["cosh"] = Math.Cosh,
        ["tanh"] = Math.Tanh,
        ["ln"] = Math.Log,
        ["log"] = Math.Log10,
        ["log2"] = Math.Log2,
        ["exp"] = Math.Exp,
        ["floor"] = Math.Floor,
        ["ceil"] = Math.Ceiling,
        ["round"] = x => Math.Round(x, MidpointRounding.AwayFromZero),
        ["trunc"] = Math.Truncate,
    };

    private static readonly Dictionary<string, double> Constants = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pi"] = Math.PI,
        ["π"] = Math.PI,
        ["e"] = Math.E,
        ["tau"] = Math.Tau,
    };

    /// <summary>
    /// True when <paramref name="text"/> is a complete expression that actually computes something and gives a
    /// finite number. A leading "=" is allowed (and forces even "=5" to count).
    /// </summary>
    public static bool TryEvaluate(string text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 500) return false;

        var parser = new Parser(text.Trim());
        bool forced = parser.Accept('=');
        try
        {
            double result = parser.ParseExpression();
            parser.SkipSpaces();
            if (!parser.AtEnd) return false;
            if (!forced && !parser.Computed) return false;
            if (double.IsNaN(result) || double.IsInfinity(result)) return false;
            value = result == 0 ? 0 : result; // no "-0"
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Up to 15 significant digits, invariant culture, so 0.1+0.2 shows 0.3 and the copy pastes anywhere.</summary>
    public static string Format(double value)
    {
        double rounded = double.Parse(value.ToString("G15", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        if (rounded == Math.Floor(rounded) && Math.Abs(rounded) < 1e15)
            return rounded.ToString("0", CultureInfo.InvariantCulture);
        return rounded.ToString("G15", CultureInfo.InvariantCulture).Replace("E+", "e", StringComparison.Ordinal)
            .Replace("E-", "e-", StringComparison.Ordinal);
    }

    private sealed class Parser(string text)
    {
        private int _pos;

        /// <summary>An operator or function was applied (a bare "42" or "(42)" computes nothing).</summary>
        public bool Computed { get; private set; }

        public bool AtEnd => _pos >= text.Length;

        private char Peek => _pos < text.Length ? text[_pos] : '\0';

        public void SkipSpaces()
        {
            while (_pos < text.Length && char.IsWhiteSpace(text[_pos])) _pos++;
        }

        public bool Accept(char c)
        {
            SkipSpaces();
            if (Peek != c) return false;
            _pos++;
            return true;
        }

        private bool AcceptWord(string word)
        {
            SkipSpaces();
            if (string.Compare(text, _pos, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;
            int end = _pos + word.Length;
            if (end < text.Length && char.IsLetter(text[end])) return false;
            _pos = end;
            return true;
        }

        // expression := term (('+' | '-') term)*
        public double ParseExpression()
        {
            double left = ParseTerm();
            while (true)
            {
                if (Accept('+')) left += ParseTerm();
                else if (Accept('-') || Accept('−')) left -= ParseTerm();
                else return left;
                Computed = true;
            }
        }

        // term := unary (('*' | '/' | 'mod' | implicit) unary)*
        private double ParseTerm()
        {
            double left = ParseUnary();
            while (true)
            {
                SkipSpaces();
                if (Peek == '*' && _pos + 1 < text.Length && text[_pos + 1] == '*') return left; // "**" is power, handled below
                if (Accept('*') || Accept('×') || Accept('·') || AcceptTimesX()) left *= ParseUnary();
                else if (Accept('/') || Accept('÷')) left /= ParseUnary();
                else if (AcceptWord("mod")) left = Modulo(left, ParseUnary());
                else if (StartsOperand()) left *= ParseUnary(); // 2pi, 3(4+1), (1+2)(3+4)
                else return left;
                Computed = true;
            }
        }

        /// <summary>"2x3" / "2 x 3": an 'x' between operands multiplies.</summary>
        private bool AcceptTimesX()
        {
            SkipSpaces();
            if (Peek is not ('x' or 'X')) return false;
            int next = _pos + 1;
            while (next < text.Length && char.IsWhiteSpace(text[next])) next++;
            if (next >= text.Length) return false;
            char c = text[next];
            if (!(char.IsDigit(c) || c is '.' or '(' or '-' or '√' or 'π' || char.IsLetter(c) && next > _pos + 1)) return false;
            _pos++;
            return true;
        }

        private bool StartsOperand()
        {
            SkipSpaces();
            char c = Peek;
            return c is '(' or '√' or 'π' || (char.IsLetter(c) && c is not ('x' or 'X') && !LooksLikeWord("mod"));
        }

        private bool LooksLikeWord(string word) =>
            string.Compare(text, _pos, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0;

        // unary := ('-' | '+' | '√') unary | power
        private double ParseUnary()
        {
            if (Accept('-') || Accept('−'))
            {
                Computed = true;
                return -ParseUnary();
            }
            if (Accept('+')) return ParseUnary();
            if (Accept('√'))
            {
                Computed = true;
                return Math.Sqrt(ParseUnary());
            }
            return ParsePower();
        }

        // power := postfix (('^' | '**') unary)?   right-associative, so 2^3^2 = 2^9 and -2^2 = -4
        private double ParsePower()
        {
            double baseValue = ParsePostfix();
            SkipSpaces();
            if (Accept('^') || (Peek == '*' && _pos + 1 < text.Length && text[_pos + 1] == '*' && Skip(2)))
            {
                Computed = true;
                return Math.Pow(baseValue, ParseUnary());
            }
            return baseValue;
        }

        private bool Skip(int count)
        {
            _pos += count;
            return true;
        }

        // postfix := primary ('!' | '%')*
        private double ParsePostfix()
        {
            double value = ParsePrimary();
            while (true)
            {
                if (Accept('!')) value = Factorial(value);
                else if (Accept('%')) value /= 100;
                else return value;
                Computed = true;
            }
        }

        // primary := number | '(' expression ')' | constant | function '(' expression ')' | function primary
        private double ParsePrimary()
        {
            SkipSpaces();
            char c = Peek;
            if (char.IsDigit(c) || c == '.') return ParseNumber();
            if (Accept('('))
            {
                double inner = ParseExpression();
                if (!Accept(')')) throw new FormatException();
                return inner;
            }
            if (c == 'π')
            {
                _pos++;
                return Math.PI;
            }
            if (char.IsLetter(c))
            {
                int start = _pos;
                while (_pos < text.Length && (char.IsLetterOrDigit(text[_pos]))) _pos++;
                string name = text[start.._pos];
                if (Functions.TryGetValue(name, out var function))
                {
                    Computed = true;
                    return function(ParsePostfixOperand());
                }
                if (Constants.TryGetValue(name, out double constant)) return constant;
                // "2pie" isn't a thing, but "pie" shouldn't parse as pi*e either: unknown words fail.
            }
            throw new FormatException();
        }

        /// <summary>
        /// A function's argument: "sqrt(2)" (so "sqrt(4)^2" is 4 and "sqrt(4)!" is 2!), or "sqrt 2" / "sin pi"
        /// without parentheses.
        /// </summary>
        private double ParsePostfixOperand()
        {
            SkipSpaces();
            if (AtEnd) throw new FormatException();
            return Peek == '(' ? ParsePrimary() : ParsePower();
        }

        private double ParseNumber()
        {
            int start = _pos;
            bool dot = false;
            while (_pos < text.Length && (char.IsDigit(text[_pos]) || (text[_pos] == '.' && !dot)))
            {
                if (text[_pos] == '.') dot = true;
                _pos++;
            }
            // Exponent: "1e3", "2.5E-4" (but "2e" alone is 2·e, handled by implicit multiplication).
            if (_pos < text.Length && text[_pos] is 'e' or 'E')
            {
                int save = _pos++;
                if (_pos < text.Length && text[_pos] is '+' or '-') _pos++;
                if (_pos < text.Length && char.IsDigit(text[_pos]))
                    while (_pos < text.Length && char.IsDigit(text[_pos])) _pos++;
                else _pos = save;
            }
            string token = text[start.._pos];
            if (token == ".") throw new FormatException();
            return double.Parse(token, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static double Modulo(double a, double b) => b == 0 ? double.NaN : a - b * Math.Floor(a / b);

        private static double Factorial(double n)
        {
            if (n < 0 || n != Math.Floor(n) || n > 170) return double.NaN;
            double result = 1;
            for (int i = 2; i <= (int)n; i++) result *= i;
            return result;
        }
    }
}
