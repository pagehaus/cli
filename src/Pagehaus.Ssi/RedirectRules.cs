using System.Text;
using System.Text.RegularExpressions;

namespace Pagehaus.Ssi;

/// <summary>Where to send a request: <see cref="Location"/> is ready for the header (printable ASCII only).</summary>
public sealed record Redirect(string Location, int Status);

/// <summary>A line of a _redirects file that was skipped, and why, in plain words.</summary>
public sealed record RedirectProblem(int Line, string Message);

/// <summary>A rule as written: the old address, the new one, and the status.</summary>
public sealed record RedirectRule(int Line, string From, string To, int Status, bool External);

/// <summary>
/// A site's _redirects file, in the format Netlify and Cloudflare Pages use, the part of it that makes sense for plain
/// sites: one rule per line, <c>/old /new [301|302]</c>, a <c>*</c> at the end of the old address with <c>:splat</c> in
/// the new one, and <c>:name</c> for one part of the path. Matching ignores case and a trailing slash; the first match
/// wins. Rules are only for addresses that would otherwise be "not found", which is up to the host. Lines that can't
/// be read are skipped and listed in <see cref="Problems"/>; the rest still work.
/// </summary>
public sealed partial class RedirectRules
{
    public const string FileName = "_redirects";
    public const int MaxRules = 1000;
    public const int MaxBytes = 64 * 1024;
    public const int MaxTargetLength = 2000;
    /// <summary>How many redirects in a row <see cref="FindLoops"/> follows before calling it a loop.</summary>
    public const int MaxHops = 10;

    public static readonly RedirectRules Empty = new([], []);

    private readonly IReadOnlyList<Rule> _rules;

    private RedirectRules(IReadOnlyList<Rule> rules, IReadOnlyList<RedirectProblem> problems)
    {
        _rules = rules;
        Problems = problems;
    }

    public IReadOnlyList<RedirectProblem> Problems { get; }

    /// <summary>The rules that work, in order.</summary>
    public IReadOnlyList<RedirectRule> Rules => _rules.Select(r => r.Written).ToList();

    public int Count => _rules.Count;

    /// <param name="externalRefused">
    /// When set, rules that send visitors to another website are skipped, with this as the problem (the host's plan
    /// doesn't allow them, say).
    /// </param>
    public static RedirectRules Parse(string text, string? externalRefused = null)
    {
        var rules = new List<Rule>();
        var problems = new List<RedirectProblem>();
        var bytes = 0;
        var lineNo = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNo++;
            bytes += Encoding.UTF8.GetByteCount(raw) + 1;
            if (bytes > MaxBytes + 1)
            {
                problems.Add(new(lineNo, "The file is over 64 KB: this line and the ones after it are skipped."));
                break;
            }
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (rules.Count == MaxRules)
            {
                problems.Add(new(lineNo, $"More than {MaxRules:N0} rules: this one and the ones after it are skipped."));
                break;
            }
            if (ParseLine(line, lineNo, externalRefused, out var problem) is { } rule) rules.Add(rule);
            else problems.Add(new(lineNo, problem!));
        }
        return new(rules, problems);
    }

    private static Rule? ParseLine(string line, int lineNo, string? externalRefused, out string? problem)
    {
        problem = null;
        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return Fail<Rule>(out problem, "A rule needs the old address and the new one, separated by a space.");
        if (parts.Length > 3)
            return Fail<Rule>(out problem, "A rule has three parts at most: the old address, the new one and the status. " +
                                     "Conditions such as Country= aren't supported.");

        var status = 301;
        if (parts.Length == 3)
        {
            var s = parts[2];
            if (s.Contains('!'))
                return Fail<Rule>(out problem, "! (redirect even where a page exists) isn't supported: a rule only applies where " +
                                         "nothing is found, so remove the page instead.");
            if (s == "200")
                return Fail<Rule>(out problem, "200 (show another page at this address) isn't supported: use 301 or 302.");
            if (s is not ("301" or "302"))
                return Fail<Rule>(out problem, "The status must be 301 (moved for good) or 302 (moved for now).");
            status = s == "301" ? 301 : 302;
        }

        if (ParseSource(parts[0], out problem) is not { } source) return null;
        if (ParseTarget(parts[1], source, out problem) is not { } target) return null;
        if (target.External && externalRefused is not null) return Fail<Rule>(out problem, externalRefused);
        if (!target.External && !source.Splat && source.Names.Count == 0 && target.Placeholders == 0
            && Key(target.PathPart) == Key(source.Segments))
            return Fail<Rule>(out problem, "The rule sends the address to itself.");

        return new Rule(new RedirectRule(lineNo, parts[0], parts[1], status, target.External), source, target, status);
    }

    private static Source? ParseSource(string text, out string? problem)
    {
        problem = null;
        if (!text.StartsWith('/'))
            return Fail<Source>(out problem, text.Contains("://")
                ? "The old address must be a path on this site, starting with /, not a full address."
                : "The old address must start with /.");
        if (text.Contains('?') || text.Contains('#'))
            return Fail<Source>(out problem, "The old address must be a path without ? or #: query strings can't be matched.");

        var segments = Uri.UnescapeDataString(text).Split('/', StringSplitOptions.RemoveEmptyEntries);
        var names = new List<string>();
        var splat = false;
        for (var i = 0; i < segments.Length; i++)
        {
            var seg = segments[i];
            if (seg.Any(c => c < 32 || c == 127 || c == '\\'))
                return Fail<Source>(out problem, "The old address has a character that can't be in an address.");
            if (seg is "." or "..")
                return Fail<Source>(out problem, "The old address can't have . or .. in it.");
            if (seg == "*" && i == segments.Length - 1)
            {
                splat = true;
                continue;
            }
            if (seg.Contains('*'))
                return Fail<Source>(out problem, "* only works as the last part of the old address, as in /blog/*.");
            if (seg.StartsWith(':'))
            {
                if (!PlaceholderName().IsMatch(seg))
                    return Fail<Source>(out problem, $"{seg} isn't a name: use letters, digits and _, starting with a letter.");
                var name = seg[1..];
                if (name.Equals("splat", StringComparison.OrdinalIgnoreCase))
                    return Fail<Source>(out problem, ":splat is for the new address; end the old one with * to match the rest.");
                if (names.Contains(name, StringComparer.OrdinalIgnoreCase))
                    return Fail<Source>(out problem, $":{name} appears twice in the old address.");
                names.Add(name);
            }
        }
        return new Source(splat ? segments[..^1] : segments, names, splat);
    }

    private static Target? ParseTarget(string text, Source source, out string? problem)
    {
        problem = null;
        if (text.Length > MaxTargetLength)
            return Fail<Target>(out problem, $"The new address is longer than {MaxTargetLength:N0} characters.");
        if (text.Any(c => c < 33 || c == 127 || c is '"' or '<' or '>' or '\\' or '`'))
            return Fail<Target>(out problem, "The new address has a character that can't be in an address (a quote, <, >, ` or \\).");

        bool external;
        if (text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
                return Fail<Target>(out problem, "The new address isn't a valid web address.");
            external = true;
        }
        else if (text.StartsWith("//") || text.StartsWith("/\\"))
            return Fail<Target>(out problem, "The new address starts with //: write https:// for another website, or a single / for this site.");
        else if (text.StartsWith('/'))
            external = false;
        else
            return Fail<Target>(out problem, "The new address must start with / (a page on this site) or https:// (another website).");

        var placeholders = 0;
        foreach (Match m in Placeholder().Matches(text))
        {
            var name = m.Groups[1].Value;
            if (name.Equals("splat", StringComparison.OrdinalIgnoreCase))
            {
                if (!source.Splat)
                    return Fail<Target>(out problem, ":splat needs an old address ending in *, as in /blog/* /posts/:splat.");
            }
            else if (!source.Names.Contains(name, StringComparer.OrdinalIgnoreCase))
                return Fail<Target>(out problem, $"The new address uses :{name}, which the old address doesn't have.");
            placeholders++;
        }

        var end = text.IndexOfAny(['?', '#']);
        var pathPart = external ? [] : Uri.UnescapeDataString(end < 0 ? text : text[..end]).Split('/', StringSplitOptions.RemoveEmptyEntries);
        return new Target(text, external, placeholders, pathPart);
    }

    /// <summary>
    /// The redirect for a request path (as received, percent-decoded) that would otherwise be "not found", or null.
    /// <paramref name="queryString"/> is the request's, with its "?"; it's passed on unless the new address has its own.
    /// </summary>
    public Redirect? Match(string path, string? queryString = null)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var rule in _rules)
            if (rule.TryMatch(segments) is { } values)
                return new Redirect(rule.Location(values, queryString), rule.Status);
        return null;
    }

    /// <summary>
    /// Rules that send visitors round in a circle: following redirects from the rule's new address comes back to it,
    /// or keeps going for more than <see cref="MaxHops"/>. <paramref name="exists"/> says whether a path is served
    /// (a file, or a folder with an index page), since a rule never applies there.
    /// </summary>
    public IReadOnlyList<RedirectProblem> FindLoops(Func<string, bool> exists)
    {
        var loops = new List<RedirectProblem>();
        foreach (var rule in _rules)
        {
            if (rule.Target.External || rule.Source.Splat || rule.Source.Names.Count > 0) continue;
            var start = Key(rule.Source.Segments);
            if (exists("/" + string.Join('/', rule.Source.Segments))) continue;   // the page is served; the rule never applies
            var path = "/" + string.Join('/', rule.Target.PathPart);
            var trail = new List<string> { rule.Written.From, path };
            for (var hop = 1; ; hop++)
            {
                if (exists(path)) break;
                var next = Match(path);
                if (next is null || IsExternal(next.Location)) break;
                path = Uri.UnescapeDataString(next.Location.Split('?', '#')[0]);
                trail.Add(path);
                if (Key(path.Split('/', StringSplitOptions.RemoveEmptyEntries)) == start || hop >= MaxHops)
                {
                    loops.Add(new(rule.Written.Line, $"The redirects go round in a circle ({string.Join(" → ", trail.Take(MaxHops + 1))}): " +
                                                    "browsers give up and show an error."));
                    break;
                }
            }
        }
        return loops;
    }

    /// <summary>
    /// One part of a request path, encoded for the new address. Hosts leave an encoded slash (%2F) as it is when they
    /// decode a path, so it stays %2F instead of becoming %252F.
    /// </summary>
    private static string EscapeSegment(string segment) =>
        Uri.EscapeDataString(segment).Replace("%252F", "%2F", StringComparison.OrdinalIgnoreCase);

    private static bool IsExternal(string location) => !location.StartsWith('/');

    private static string Key(IEnumerable<string> segments) => string.Join('/', segments).ToLowerInvariant();

    private static T? Fail<T>(out string? problem, string message) where T : class
    {
        problem = message;
        return null;
    }

    /// <summary>Percent-encodes anything that can't go in a header as it is (non-ASCII, spaces, controls, quotes).</summary>
    private static string Encode(string text)
    {
        var sb = new StringBuilder(text.Length);
        Span<byte> utf8 = stackalloc byte[4];
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value is > 32 and < 127 && rune.Value is not ('"' or '<' or '>' or '\\' or '`' or '{' or '}' or '|' or '^'))
            {
                sb.Append((char)rune.Value);
                continue;
            }
            var n = rune.EncodeToUtf8(utf8);
            foreach (var b in utf8[..n]) sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    [GeneratedRegex("^:[A-Za-z][A-Za-z0-9_]*$")]
    private static partial Regex PlaceholderName();

    [GeneratedRegex(":([A-Za-z][A-Za-z0-9_]*)")]
    private static partial Regex Placeholder();

    private sealed record Source(string[] Segments, List<string> Names, bool Splat);

    private sealed record Target(string Text, bool External, int Placeholders, string[] PathPart);

    private sealed record Rule(RedirectRule Written, Source Source, Target Target, int Status)
    {
        /// <summary>The placeholder values (":splat" included) if <paramref name="segments"/> matches, or null.</summary>
        public Dictionary<string, string>? TryMatch(string[] segments)
        {
            var fixedCount = Source.Segments.Length;
            if (Source.Splat ? segments.Length < fixedCount : segments.Length != fixedCount) return null;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < fixedCount; i++)
            {
                var pattern = Source.Segments[i];
                if (pattern.StartsWith(':') && PlaceholderName().IsMatch(pattern))
                    values[pattern[1..]] = EscapeSegment(segments[i]);
                else if (!pattern.Equals(segments[i], StringComparison.OrdinalIgnoreCase))
                    return null;
            }
            if (Source.Splat)
                values["splat"] = string.Join('/', segments[fixedCount..].Select(EscapeSegment));
            return values;
        }

        public string Location(Dictionary<string, string> values, string? queryString)
        {
            var location = Target.Placeholders == 0
                ? Target.Text
                : Placeholder().Replace(Target.Text, m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
            if (!string.IsNullOrEmpty(queryString) && queryString != "?" && !location.Contains('?'))
            {
                var query = queryString.StartsWith('?') ? queryString : "?" + queryString;
                var hash = location.IndexOf('#');
                location = hash < 0 ? location + query : location[..hash] + query + location[hash..];
            }
            return Encode(location);
        }
    }
}
