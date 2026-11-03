using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Pagehaus.Ssi;

/// <summary>
/// A condition for <c>#if</c> and <c>#elif</c>, in Apache's long-standing ("legacy") syntax: strings with <c>$NAME</c>
/// variables, <c>=</c> <c>==</c> <c>!=</c> <c>&lt;</c> <c>&lt;=</c> <c>&gt;</c> <c>&gt;=</c>, <c>/regex/</c> on the right of
/// <c>=</c> or <c>!=</c>, and <c>!</c> <c>&amp;&amp;</c> <c>||</c> with brackets. Apache 2.4's newer syntax
/// (<c>v('NAME')</c>, <c>%{NAME}</c>, <c>-z</c>, <c>=~</c>) is refused rather than read as plain strings, which would make
/// a condition quietly wrong.
/// </summary>
public sealed class SsiExpression
{
    public const int MaxLength = 1000;
    public const int MaxPatternLength = 200;
    private const int MaxCached = 1000;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private static readonly ConcurrentDictionary<string, SsiExpression> Cache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Regex> Patterns = new(StringComparer.Ordinal);

    private readonly Node _root;
    private SsiExpression(Node root) => _root = root;

    /// <summary>Parses an expression, from a cache: a shared header repeats the same few on every page.</summary>
    /// <exception cref="FormatException">The expression can't be read; the message says why.</exception>
    public static SsiExpression Parse(string text)
    {
        if (Cache.TryGetValue(text, out var cached)) return cached;
        if (text.Length > MaxLength) throw new FormatException($"longer than {MaxLength} characters");
        var parsed = new SsiExpression(new Parser(Lex(text)).ParseAll());
        if (Cache.Count >= MaxCached) Cache.Clear();
        Cache[text] = parsed;
        return parsed;
    }

    /// <param name="variable">Looks up a variable's value; null when it isn't set.</param>
    /// <param name="captures">Receives a regular expression's groups ($0 to $9) after each match.</param>
    public bool Evaluate(Func<string, string?> variable, Action<string[]> captures) => _root.Evaluate(variable, captures);

    // Tokens ------------------------------------------------------------------------------------------------------------

    private enum Kind { Text, Pattern, Op, Open, Close, Not, And, Or }

    /// <param name="Quoted">A quoted string's text stays as written; variables are still substituted, as in Apache.</param>
    private sealed record Token(Kind Kind, string Value, bool Quoted = false);

    private static List<Token> Lex(string s)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '(') { tokens.Add(new(Kind.Open, "(")); i++; continue; }
            if (c == ')') { tokens.Add(new(Kind.Close, ")")); i++; continue; }
            if (At(s, i, "&&")) { tokens.Add(new(Kind.And, "&&")); i += 2; continue; }
            if (At(s, i, "||")) { tokens.Add(new(Kind.Or, "||")); i += 2; continue; }
            if (At(s, i, "=~") || At(s, i, "!~")) throw Newer();
            if (At(s, i, "==") || At(s, i, "!=") || At(s, i, "<=") || At(s, i, ">="))
            {
                tokens.Add(new(Kind.Op, s.Substring(i, 2))); i += 2; continue;
            }
            if (c is '=' or '<' or '>') { tokens.Add(new(Kind.Op, c.ToString())); i++; continue; }
            if (c == '!') { tokens.Add(new(Kind.Not, "!")); i++; continue; }
            if (c == '&' || c == '|') throw new FormatException($"a single '{c}': use && or ||");

            if (c == '\'')
            {
                var sb = new StringBuilder();
                var j = i + 1;
                for (; j < s.Length && s[j] != '\''; j++)
                {
                    if (s[j] == '\\' && j + 1 < s.Length && s[j + 1] == '\'') j++;
                    sb.Append(s[j]);
                }
                if (j >= s.Length) throw new FormatException("a quote (') that isn't closed");
                tokens.Add(new(Kind.Text, sb.ToString(), Quoted: true));
                i = j + 1;
                continue;
            }

            // /pattern/: up to the next unescaped slash that ends the word, so a pattern may hold spaces, brackets and |.
            // A path such as /about.html has no closing slash and is plain text.
            if (c == '/' && PatternEnd(s, i) is { } end)
            {
                tokens.Add(new(Kind.Pattern, s.Substring(i + 1, end - i - 1)));
                i = end + 1;
                continue;
            }

            var start = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] is not ('(' or ')' or '=' or '<' or '>' or '!' or '\'')
                   && !At(s, i, "&&") && !At(s, i, "||"))
                i += s[i] == '\\' && i + 1 < s.Length ? 2 : 1;
            var word = s[start..i];
            // %{NAME}, -z and the like, and function calls such as v('NAME') or tolower($X).
            if (word.StartsWith("%{", StringComparison.Ordinal) || IsNewerOperator(word) || (i < s.Length && s[i] == '('))
                throw Newer();
            tokens.Add(new(Kind.Text, word));
        }
        return tokens;
    }

    private static bool At(string s, int i, string what) => string.CompareOrdinal(s, i, what, 0, what.Length) == 0;

    private static int? PatternEnd(string s, int start)
    {
        for (var j = start + 1; j < s.Length; j++)
        {
            if (s[j] == '\\') { j++; continue; }
            if (s[j] != '/') continue;
            var next = j + 1;
            if (next == s.Length || char.IsWhiteSpace(s[next]) || s[next] == ')' || At(s, next, "&&") || At(s, next, "||"))
                return j;
        }
        return null;
    }

    // -z, -n, -f, -strmatch and the like: Apache 2.4's newer syntax. Nobody writes them as text.
    private static bool IsNewerOperator(string word) =>
        word.Length > 1 && word[0] == '-' && word.Skip(1).All(char.IsAsciiLetter);

    private static FormatException Newer() =>
        new("written in Apache 2.4's newer syntax, which isn't supported: compare with $NAME = value, or $NAME = /pattern/");

    // Syntax tree ---------------------------------------------------------------------------------------------------------

    private abstract record Node
    {
        public abstract bool Evaluate(Func<string, string?> variable, Action<string[]> captures);
    }

    private sealed record Or(Node Left, Node Right) : Node
    {
        public override bool Evaluate(Func<string, string?> v, Action<string[]> c) => Left.Evaluate(v, c) || Right.Evaluate(v, c);
    }

    private sealed record And(Node Left, Node Right) : Node
    {
        public override bool Evaluate(Func<string, string?> v, Action<string[]> c) => Left.Evaluate(v, c) && Right.Evaluate(v, c);
    }

    private sealed record Not(Node Inner) : Node
    {
        public override bool Evaluate(Func<string, string?> v, Action<string[]> c) => !Inner.Evaluate(v, c);
    }

    /// <summary>A string alone: true when it isn't empty.</summary>
    private sealed record NotEmpty(Text Value) : Node
    {
        public override bool Evaluate(Func<string, string?> v, Action<string[]> c) => Value.Resolve(v).Length > 0;
    }

    private sealed record Compare(Text Left, string Op, Text Right) : Node
    {
        public override bool Evaluate(Func<string, string?> v, Action<string[]> c)
        {
            var cmp = string.CompareOrdinal(Left.Resolve(v), Right.Resolve(v));
            return Op switch
            {
                "=" or "==" => cmp == 0,
                "!=" => cmp != 0,
                "<" => cmp < 0,
                "<=" => cmp <= 0,
                ">" => cmp > 0,
                _ => cmp >= 0,
            };
        }
    }

    private sealed record Matches(Text Left, bool Negate, Regex Pattern) : Node
    {
        public override bool Evaluate(Func<string, string?> v, Action<string[]> c)
        {
            Match m;
            try
            {
                m = Pattern.Match(Left.Resolve(v));
            }
            catch (RegexMatchTimeoutException)
            {
                return Negate; // as if it didn't match
            }
            if (m.Success) c(Enumerable.Range(0, 10).Select(g => g < m.Groups.Count ? m.Groups[g].Value : "").ToArray());
            return m.Success != Negate;
        }
    }

    /// <summary>One or more strings side by side, joined by a space as in Apache, with variables substituted.</summary>
    private sealed record Text(IReadOnlyList<string> Parts)
    {
        public string Resolve(Func<string, string?> variable) =>
            string.Join(" ", Parts.Select(p => SsiProcessor.SubstituteVariables(p, variable)));
    }

    private sealed class Parser(List<Token> tokens)
    {
        private int _pos;

        private Token? Peek => _pos < tokens.Count ? tokens[_pos] : null;

        public Node ParseAll()
        {
            if (tokens.Count == 0) throw new FormatException("empty");
            var node = ParseOr();
            if (Peek is { } extra) throw new FormatException($"unexpected '{extra.Value}'");
            return node;
        }

        private Node ParseOr()
        {
            var left = ParseAnd();
            while (Peek?.Kind == Kind.Or) { _pos++; left = new Or(left, ParseAnd()); }
            return left;
        }

        private Node ParseAnd()
        {
            var left = ParseUnary();
            while (Peek?.Kind == Kind.And) { _pos++; left = new And(left, ParseUnary()); }
            return left;
        }

        private Node ParseUnary()
        {
            if (Peek?.Kind == Kind.Not) { _pos++; return new Not(ParseUnary()); }
            if (Peek?.Kind == Kind.Open)
            {
                _pos++;
                var inner = ParseOr();
                if (Peek?.Kind != Kind.Close) throw new FormatException("a bracket that isn't closed");
                _pos++;
                return inner;
            }
            var left = ParseText() ?? throw new FormatException(Peek is null ? "it ends too soon" : $"unexpected '{Peek.Value}'");
            if (Peek?.Kind != Kind.Op) return new NotEmpty(left);
            var op = tokens[_pos++].Value;
            if (Peek?.Kind == Kind.Pattern)
            {
                if (op is not ("=" or "==" or "!=")) throw new FormatException($"a /pattern/ after '{op}': use = or !=");
                return new Matches(left, op == "!=", Compile(tokens[_pos++].Value));
            }
            var right = ParseText() ?? throw new FormatException($"nothing to compare after '{op}'");
            return new Compare(left, op, right);
        }

        private Text? ParseText()
        {
            var parts = new List<string>();
            while (Peek is { Kind: Kind.Text } t) { parts.Add(t.Value); _pos++; }
            if (Peek?.Kind == Kind.Pattern) throw new FormatException("a /pattern/ only goes after = or !=");
            return parts.Count == 0 ? null : new Text(parts);
        }
    }

    private static Regex Compile(string pattern)
    {
        if (pattern.Length > MaxPatternLength) throw new FormatException($"a pattern longer than {MaxPatternLength} characters");
        if (Patterns.TryGetValue(pattern, out var cached)) return cached;
        Regex regex;
        try
        {
            // The non-backtracking engine can't be made to run long. Patterns it can't run (backreferences, lookaround)
            // use the usual engine with a time limit.
            try
            {
                regex = new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, RegexTimeout);
            }
            catch (NotSupportedException)
            {
                regex = new Regex(pattern, RegexOptions.CultureInvariant, RegexTimeout);
            }
        }
        catch (ArgumentException ex)
        {
            throw new FormatException("a pattern that isn't a valid regular expression: " + ex.Message);
        }
        if (Patterns.Count >= MaxCached) Patterns.Clear();
        Patterns[pattern] = regex;
        return regex;
    }
}
