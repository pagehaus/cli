using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Pagehaus.Ssi;

/// <summary>A mistake <see cref="SsiProcessor.Lint"/> found, with the line it's on.</summary>
public sealed record SsiLintProblem(int Line, string Message);

/// <summary>Per-request state for SSI processing. Plain values so it can be used without an HttpContext.</summary>
public sealed class SsiContext
{
    public required string SiteRoot { get; init; }

    /// <summary>Site-relative path of the page being served, e.g. "blog/index.html".</summary>
    public required string DocumentPath { get; init; }

    public string Host { get; init; } = "";
    public string QueryString { get; init; } = "";

    /// <summary>
    /// The address the visitor asked for, without the query string (<c>REQUEST_URI</c>): "/blog/" where
    /// <see cref="DocumentPath"/> is "blog/index.html", or the missing address on the 404 page. Null means the page's own.
    /// </summary>
    public string? RequestPath { get; init; }

    /// <summary>
    /// Whether includes may go through symbolic links. Off by default: on a host, a link could point anywhere, another
    /// site's password-protected files included. On your own computer (pagehaus serve) links are yours to use.
    /// </summary>
    public bool FollowLinks { get; init; }
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Local;
    public DateTimeOffset Now { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// The site's visitor total for <c>#counter</c>, asked at most once per page. Null where there's no total to show:
    /// the directive is then an error, rather than a made-up number.
    /// </summary>
    public Func<ValueTask<long>>? VisitorCount { get; init; }

    internal Dictionary<string, string> Vars { get; } = new(StringComparer.Ordinal);
    internal string TimeFmt { get; set; } = "%A, %d-%b-%Y %H:%M:%S %Z";
    internal string SizeFmt { get; set; } = "abbrev";
    internal string ErrMsg { get; set; } = "[an error occurred while processing this directive]";
    internal string EchoMsg { get; set; } = "(none)";
    internal int OutputBytes { get; set; }
    internal long? Visitors { get; set; }

    /// <summary>The groups of the last regular expression matched in a condition: $0 to $9.</summary>
    internal string[]? Captures { get; set; }
    public int Directives { get; internal set; }
}

/// <summary>
/// Server Side Includes, Apache mod_include style, without #exec.
/// Supported: include (virtual/file), echo, set, config (timefmt/sizefmt/errmsg/echomsg), flastmod, fsize,
/// if/elif/else/endif (<see cref="SsiExpression"/>); and counter, a visitor counter, where the host provides
/// <see cref="SsiContext.VisitorCount"/>.
/// </summary>
public sealed partial class SsiProcessor
{
    public const int MaxDepth = 8;
    public const int MaxDirectives = 5000;
    public const int MaxIncludeBytes = 2 * 1024 * 1024;
    public const int MaxOutputBytes = 5 * 1024 * 1024;
    public const int MaxConditionDepth = 32;

    /// <summary>
    /// Pages larger than this are sent as they are, without processing includes: processing reads the whole page into
    /// memory, and a page this big is media packed into HTML (base64 videos), not a page that uses includes.
    /// </summary>
    public const int MaxPageBytes = 2 * 1024 * 1024;

    // Quoted values may contain the quote character escaped with a backslash, as in Apache: value="say \"hi\"".
    [GeneratedRegex("""<!--#([a-zA-Z]+)((?:\s+[a-zA-Z_]+\s*=\s*(?:"(?:[^"\\]|\\.)*"|'(?:[^'\\]|\\.)*'))*)\s*-->""")]
    private static partial Regex DirectiveRegex();

    [GeneratedRegex("""([a-zA-Z_]+)\s*=\s*(?:"((?:[^"\\]|\\.)*)"|'((?:[^'\\]|\\.)*)')""")]
    private static partial Regex AttributeRegex();

    // \$ is a literal dollar sign, as in Apache. $0 to $9 are the groups of the last pattern matched in a condition.
    [GeneratedRegex(@"\\\$|\$\{([A-Za-z_][A-Za-z0-9_]*|[0-9])\}|\$([A-Za-z_][A-Za-z0-9_]*|[0-9])")]
    private static partial Regex VariableRefRegex();

    public static bool IsSsiFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".html" or ".htm" or ".shtml";

    public Task<string> ProcessAsync(string content, SsiContext ctx) =>
        ProcessAsync(content, ctx, ctx.DocumentPath, depth: 0);

    private async Task<string> ProcessAsync(string content, SsiContext ctx, string currentFile, int depth)
    {
        if (!content.Contains("<!--#", StringComparison.Ordinal)) return content;

        var sb = new StringBuilder(content.Length);
        var pos = 0;
        // Each file has its own conditions: an #if opened in an include closes there, so a broken header can't hide the
        // rest of the page.
        var conditions = new Stack<Condition>();
        var showing = true;
        foreach (Match m in DirectiveRegex().Matches(content))
        {
            if (showing) sb.Append(content, pos, m.Index - pos);
            pos = m.Index + m.Length;

            // Total directives across the whole request are capped, so deeply nested includes can't amplify CPU.
            if (++ctx.Directives > MaxDirectives) return sb.Append(ctx.ErrMsg).ToString();

            var attrs = Attributes(m);
            var directive = m.Groups[1].Value.ToLowerInvariant();

            string? result;
            if (directive is "if" or "elif" or "else" or "endif")
            {
                result = Branch(directive, attrs, conditions, ctx, currentFile);
                showing = conditions.Count == 0 || conditions.Peek().Showing;
            }
            else if (!showing)
            {
                continue; // a branch that isn't shown: nothing in it runs
            }
            else
            {
                try
                {
                    result = await RunDirectiveAsync(directive, attrs, ctx, currentFile, depth) ?? ctx.ErrMsg;
                }
                catch (IOException)
                {
                    result = ctx.ErrMsg;
                }
            }
            if (result is null) continue;

            ctx.OutputBytes += result.Length;
            if (ctx.OutputBytes > MaxOutputBytes) return sb.Append(ctx.ErrMsg).ToString();
            sb.Append(result);
        }
        if (showing) sb.Append(content, pos, content.Length - pos);
        // A missing #endif: what followed was shown or not as its branch said, and the mistake shows at the end.
        if (conditions.Count > 0) sb.Append(ctx.ErrMsg);
        return sb.ToString();
    }

    private static List<(string Name, string Value)> Attributes(Match directive) =>
        AttributeRegex().Matches(directive.Groups[2].Value)
            .Select(a => (Name: a.Groups[1].Value.ToLowerInvariant(),
                          Value: a.Groups[2].Success ? Unescape(a.Groups[2].Value, '"') : Unescape(a.Groups[3].Value, '\'')))
            .ToList();

    /// <summary>
    /// What reading a file finds without serving it: conditions that don't balance, expressions that can't be read, and
    /// files included (or measured with flastmod/fsize) inside a condition that don't exist. Those are in a branch that
    /// the pages checked may never show, so processing them wouldn't notice; outside conditions, processing does.
    /// </summary>
    /// <param name="currentFile">Site-relative path of the file, for relative includes.</param>
    public static IReadOnlyList<SsiLintProblem> Lint(string content, string siteRoot, string currentFile, bool followLinks = false)
    {
        var problems = new List<SsiLintProblem>();
        if (!content.Contains("<!--#", StringComparison.Ordinal)) return problems;
        var ctx = new SsiContext { SiteRoot = siteRoot, DocumentPath = currentFile, FollowLinks = followLinks };
        var open = new Stack<(int Line, bool SeenElse)>();
        foreach (Match m in DirectiveRegex().Matches(content))
        {
            var line = 1 + content.AsSpan(0, m.Index).Count('\n');
            void Add(string message) => problems.Add(new SsiLintProblem(line, message));
            var directive = m.Groups[1].Value.ToLowerInvariant();
            var attrs = Attributes(m);
            switch (directive)
            {
                case "if" or "elif":
                    if (directive == "if")
                    {
                        open.Push((line, false));
                        if (open.Count == MaxConditionDepth + 1) Add($"#if nested more than {MaxConditionDepth} deep.");
                    }
                    else if (open.Count == 0) { Add("#elif without an #if before it."); break; }
                    else if (open.Peek().SeenElse) Add("#elif after #else: it's never used.");
                    if (attrs is not [("expr", var expr)]) { Add($"#{directive} needs expr=\"...\"."); break; }
                    try { SsiExpression.Parse(expr); }
                    catch (FormatException ex) { Add($"#{directive} expr=\"{expr}\" can't be read: {ex.Message}."); }
                    break;
                case "else":
                    if (open.Count == 0) { Add("#else without an #if before it."); break; }
                    if (open.Peek().SeenElse) Add("a second #else for the same #if.");
                    open.Push((open.Pop().Line, true));
                    break;
                case "endif":
                    if (open.Count == 0) Add("#endif without an #if before it.");
                    else open.Pop();
                    break;
                case "include" or "flastmod" or "fsize" when open.Count > 0 && attrs.Count > 0:
                    var target = ResolveTarget(attrs[0], ctx, currentFile);
                    if (target is null || !File.Exists(SitePath.ToDisk(siteRoot, target)))
                        Add($"#{directive} {attrs[0].Name}=\"{attrs[0].Value}\" (inside an #if): there's no such file.");
                    break;
            }
        }
        foreach (var (line, _) in open)
            problems.Add(new SsiLintProblem(line, "#if without an #endif to close it."));
        return problems.OrderBy(p => p.Line).ToList();
    }

    /// <param name="ParentShowing">Whether the text around this #if is shown; if not, none of its branches are.</param>
    /// <param name="Taken">A branch has been shown (or the #if failed), so later ones aren't.</param>
    private sealed class Condition(bool parentShowing)
    {
        public bool ParentShowing { get; } = parentShowing;
        public bool Taken { get; set; }
        public bool Showing { get; set; }
        public bool SeenElse { get; set; }
    }

    /// <summary>Runs #if, #elif, #else or #endif.</summary>
    /// <returns>The error message for a mistake, otherwise null (nothing to write).</returns>
    private static string? Branch(string directive, List<(string Name, string Value)> attrs, Stack<Condition> conditions,
        SsiContext ctx, string currentFile)
    {
        var showing = conditions.Count == 0 || conditions.Peek().Showing;
        if (directive == "if")
        {
            var condition = new Condition(showing);
            conditions.Push(condition);
            if (!showing) return null;
            if (conditions.Count > MaxConditionDepth) { condition.Taken = true; return ctx.ErrMsg; }
            return Test(condition, attrs, ctx, currentFile);
        }

        if (conditions.Count == 0) return ctx.ErrMsg; // #elif, #else or #endif without an #if
        var current = conditions.Peek();
        switch (directive)
        {
            case "endif":
                conditions.Pop();
                return null;
            case "else":
                if (current.SeenElse) { current.Showing = false; current.Taken = true; return current.ParentShowing ? ctx.ErrMsg : null; }
                current.SeenElse = true;
                current.Showing = current.ParentShowing && !current.Taken;
                current.Taken = true;
                return null;
            default: // elif
                if (current.SeenElse) { current.Showing = false; return current.ParentShowing ? ctx.ErrMsg : null; }
                if (!current.ParentShowing || current.Taken) { current.Showing = false; return null; }
                return Test(current, attrs, ctx, currentFile);
        }
    }

    /// <summary>Evaluates an #if or #elif's expr and shows its branch if true. A mistake shows no branch of it.</summary>
    private static string? Test(Condition condition, List<(string Name, string Value)> attrs, SsiContext ctx, string currentFile)
    {
        bool result;
        try
        {
            if (attrs is not [("expr", var expr)]) throw new FormatException("needs expr=\"...\"");
            result = SsiExpression.Parse(expr).Evaluate(name => GetVariable(name, ctx, currentFile), groups => ctx.Captures = groups);
        }
        catch (FormatException)
        {
            condition.Showing = false;
            condition.Taken = true;
            return ctx.ErrMsg;
        }
        condition.Showing = result;
        condition.Taken = result;
        return null;
    }

    /// <returns>The replacement text, or null to emit the error message.</returns>
    private async Task<string?> RunDirectiveAsync(
        string directive, List<(string Name, string Value)> attrs, SsiContext ctx, string currentFile, int depth)
    {
        switch (directive)
        {
            case "include":
            {
                if (depth >= MaxDepth || attrs.Count == 0) return null;
                var target = ResolveTarget(attrs[0], ctx, currentFile);
                if (target is null) return null;
                var disk = SitePath.ToDisk(ctx.SiteRoot, target);
                var info = Info(disk, ctx);
                if (!info.Exists || info.Length > MaxIncludeBytes || Linked(ctx, target)) return null;
                var text = await File.ReadAllTextAsync(disk);
                return IsSsiFile(target) ? await ProcessAsync(text, ctx, target, depth + 1) : text;
            }

            case "echo":
            {
                var name = attrs.FirstOrDefault(a => a.Name == "var").Value;
                if (name is null) return null;
                var value = GetVariable(name, ctx, currentFile) ?? ctx.EchoMsg;
                var encoding = attrs.FirstOrDefault(a => a.Name == "encoding").Value ?? "entity";
                return encoding switch
                {
                    "none" => value,
                    "url" => Uri.EscapeDataString(value),
                    _ => WebUtility.HtmlEncode(value),
                };
            }

            case "set":
            {
                string? name = null;
                foreach (var (attr, value) in attrs)
                {
                    if (attr == "var") name = value;
                    else if (attr == "value" && name is not null)
                        ctx.Vars[name] = Substitute(value, ctx, currentFile);
                }
                return "";
            }

            case "config":
                foreach (var (attr, value) in attrs)
                {
                    switch (attr)
                    {
                        case "timefmt": ctx.TimeFmt = value; break;
                        case "sizefmt": ctx.SizeFmt = value; break;
                        case "errmsg": ctx.ErrMsg = value; break;
                        // What echo prints for an undefined variable (Apache 2.4). The blog's head line uses "".
                        case "echomsg": ctx.EchoMsg = value; break;
                    }
                }
                return "";

            case "flastmod":
            case "fsize":
            {
                if (attrs.Count == 0) return null;
                var target = ResolveTarget(attrs[0], ctx, currentFile);
                if (target is null) return null;
                var info = Info(SitePath.ToDisk(ctx.SiteRoot, target), ctx);
                if (!info.Exists || Linked(ctx, target)) return null;
                return directive == "fsize"
                    ? FormatSize(info.Length, ctx.SizeFmt)
                    : Strftime.Format(ToZone(info.LastWriteTimeUtc, ctx), ctx.TimeFmt);
            }

            case "counter":
            {
                if (ctx.VisitorCount is null || CounterStyle.Parse(attrs, currentFile) is not { } style) return null;
                ctx.Visitors ??= await ctx.VisitorCount();
                return style.Render(ctx.Visitors.Value);
            }

            default:
                // #exec and #printenv are deliberately unsupported.
                return null;
        }
    }

    /// <summary>virtual= is a URL path (absolute or relative to the page); file= is relative to the current file and may not go up.</summary>
    private static string? ResolveTarget((string Name, string Value) attr, SsiContext ctx, string currentFile)
    {
        var dir = SitePath.Parent(currentFile);
        switch (attr.Name)
        {
            case "virtual":
            {
                var value = attr.Value;
                var q = value.IndexOfAny(['?', '#']);
                if (q >= 0) value = value[..q];
                var resolved = SitePath.Combine(dir, Uri.UnescapeDataString(value));
                if (resolved is null) return null;
                var disk = SitePath.ToDisk(ctx.SiteRoot, resolved);
                if (Directory.Exists(disk))
                    resolved = SitePath.FindIndex(ctx.SiteRoot, resolved);
                return resolved;
            }
            case "file":
                if (attr.Value.StartsWith('/') || attr.Value.Split('/', '\\').Contains("..")) return null;
                return SitePath.Combine(dir, attr.Value);
            default:
                return null;
        }
    }

    private static string? GetVariable(string name, SsiContext ctx, string currentFile)
    {
        if (ctx.Vars.TryGetValue(name, out var v)) return v;
        if (name.Length == 1 && char.IsAsciiDigit(name[0])) return ctx.Captures?[name[0] - '0'];
        return name switch
        {
            "DATE_LOCAL" => Strftime.Format(TimeZoneInfo.ConvertTime(ctx.Now, ctx.TimeZone), ctx.TimeFmt),
            "DATE_GMT" => Strftime.Format(ctx.Now.ToUniversalTime(), ctx.TimeFmt),
            "DOCUMENT_NAME" => Path.GetFileName(ctx.DocumentPath),
            "DOCUMENT_URI" => "/" + ctx.DocumentPath,
            // A page that isn't saved yet (the blog editor's preview) was last modified now.
            "LAST_MODIFIED" => Strftime.Format(
                File.Exists(SitePath.ToDisk(ctx.SiteRoot, ctx.DocumentPath)) && !Linked(ctx, ctx.DocumentPath)
                    ? ToZone(Info(SitePath.ToDisk(ctx.SiteRoot, ctx.DocumentPath), ctx).LastWriteTimeUtc, ctx)
                    : TimeZoneInfo.ConvertTime(ctx.Now, ctx.TimeZone), ctx.TimeFmt),
            "QUERY_STRING" => ctx.QueryString.TrimStart('?'),
            "REQUEST_URI" => ctx.RequestPath ?? "/" + ctx.DocumentPath,
            "HTTP_HOST" or "SERVER_NAME" => ctx.Host,
            _ => null,
        };
    }

    private static bool Linked(SsiContext ctx, string target) => !ctx.FollowLinks && SitePath.HasLink(ctx.SiteRoot, target);

    /// <summary>The file's details; through a link (when links are followed), those of the file it points to.</summary>
    private static FileInfo Info(string disk, SsiContext ctx)
    {
        var info = new FileInfo(disk);
        return ctx.FollowLinks && info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is FileInfo target
            ? target
            : info;
    }

    private static string Substitute(string value, SsiContext ctx, string currentFile) =>
        SubstituteVariables(value, name => GetVariable(name, ctx, currentFile));

    /// <summary>Replaces $NAME and ${NAME} with their values (unset ones with nothing), and \$ with a dollar sign.</summary>
    internal static string SubstituteVariables(string value, Func<string, string?> variable) =>
        !value.Contains('$') ? value : VariableRefRegex().Replace(value, m => m.Value == "\\$" ? "$" :
            variable(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) ?? "");

    /// <summary>\" (or \') is the quote character and \\ a backslash. Other backslashes stay, so \$ reaches <see cref="Substitute"/>.</summary>
    private static string Unescape(string value, char quote) =>
        !value.Contains('\\') ? value : value.Replace("\\\\", "\0").Replace("\\" + quote, quote.ToString()).Replace("\0", "\\");

    private static DateTimeOffset ToZone(DateTime utc, SsiContext ctx) =>
        TimeZoneInfo.ConvertTime(new DateTimeOffset(utc, TimeSpan.Zero), ctx.TimeZone);

    private static string FormatSize(long bytes, string sizefmt)
    {
        if (sizefmt == "bytes") return bytes.ToString("N0", CultureInfo.InvariantCulture);
        if (bytes < 1024) return bytes + "";
        if (bytes < 1024 * 1024) return Math.Round(bytes / 1024.0) + "K";
        return Math.Round(bytes / (1024.0 * 1024.0), 1).ToString(CultureInfo.InvariantCulture) + "M";
    }
}

/// <summary>Just enough of C strftime for SSI timefmt.</summary>
public static class Strftime
{
    public static string Format(DateTimeOffset t, string fmt)
    {
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        for (var i = 0; i < fmt.Length; i++)
        {
            if (fmt[i] != '%' || i == fmt.Length - 1) { sb.Append(fmt[i]); continue; }
            var spec = fmt[++i];
            sb.Append(spec switch
            {
                'a' => t.ToString("ddd", c),
                'A' => t.ToString("dddd", c),
                'b' or 'h' => t.ToString("MMM", c),
                'B' => t.ToString("MMMM", c),
                'c' => t.ToString("ddd MMM d HH:mm:ss yyyy", c),
                'd' => t.ToString("dd", c),
                'D' => t.ToString("MM/dd/yy", c),
                'e' => t.Day.ToString(c).PadLeft(2),
                'F' => t.ToString("yyyy-MM-dd", c),
                'H' => t.ToString("HH", c),
                'I' => t.ToString("hh", c),
                'j' => t.DayOfYear.ToString("000", c),
                'm' => t.ToString("MM", c),
                'M' => t.ToString("mm", c),
                'p' => t.ToString("tt", c),
                'S' => t.ToString("ss", c),
                'T' => t.ToString("HH:mm:ss", c),
                'x' => t.ToString("MM/dd/yy", c),
                'X' => t.ToString("HH:mm:ss", c),
                'y' => t.ToString("yy", c),
                'Y' => t.ToString("yyyy", c),
                'z' => t.ToString("zzz", c).Replace(":", ""),
                'Z' => t.Offset == TimeSpan.Zero ? "UTC" : t.ToString("zzz", c),
                '%' => "%",
                _ => "%" + spec,
            });
        }
        return sb.ToString();
    }
}
