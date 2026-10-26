using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Pagehaus.Ssi;

/// <summary>Per-request state for SSI processing. Plain values so it can be used without an HttpContext.</summary>
public sealed class SsiContext
{
    public required string SiteRoot { get; init; }

    /// <summary>Site-relative path of the page being served, e.g. "blog/index.html".</summary>
    public required string DocumentPath { get; init; }

    public string Host { get; init; } = "";
    public string QueryString { get; init; } = "";

    /// <summary>
    /// Whether includes may go through symbolic links. Off by default: on a host, a link could point anywhere, another
    /// site's password-protected files included. On your own computer (pagehaus serve) links are yours to use.
    /// </summary>
    public bool FollowLinks { get; init; }
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Local;
    public DateTimeOffset Now { get; init; } = DateTimeOffset.UtcNow;

    internal Dictionary<string, string> Vars { get; } = new(StringComparer.Ordinal);
    internal string TimeFmt { get; set; } = "%A, %d-%b-%Y %H:%M:%S %Z";
    internal string SizeFmt { get; set; } = "abbrev";
    internal string ErrMsg { get; set; } = "[an error occurred while processing this directive]";
    internal string EchoMsg { get; set; } = "(none)";
    internal int OutputBytes { get; set; }
    public int Directives { get; internal set; }
}

/// <summary>
/// Server Side Includes, Apache mod_include style, without #exec.
/// Supported: include (virtual/file), echo, set, config (timefmt/sizefmt/errmsg/echomsg), flastmod, fsize.
/// </summary>
public sealed partial class SsiProcessor
{
    public const int MaxDepth = 8;
    public const int MaxDirectives = 5000;
    public const int MaxIncludeBytes = 2 * 1024 * 1024;
    public const int MaxOutputBytes = 5 * 1024 * 1024;

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

    // \$ is a literal dollar sign, as in Apache.
    [GeneratedRegex(@"\\\$|\$\{([A-Za-z_][A-Za-z0-9_]*)\}|\$([A-Za-z_][A-Za-z0-9_]*)")]
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
        foreach (Match m in DirectiveRegex().Matches(content))
        {
            sb.Append(content, pos, m.Index - pos);
            pos = m.Index + m.Length;

            // Total directives across the whole request are capped, so deeply nested includes can't amplify CPU.
            if (++ctx.Directives > MaxDirectives) return sb.Append(ctx.ErrMsg).ToString();

            var attrs = AttributeRegex().Matches(m.Groups[2].Value)
                .Select(a => (Name: a.Groups[1].Value.ToLowerInvariant(),
                              Value: a.Groups[2].Success ? Unescape(a.Groups[2].Value, '"') : Unescape(a.Groups[3].Value, '\'')))
                .ToList();

            string result;
            try
            {
                result = await RunDirectiveAsync(m.Groups[1].Value.ToLowerInvariant(), attrs, ctx, currentFile, depth)
                         ?? ctx.ErrMsg;
            }
            catch (IOException)
            {
                result = ctx.ErrMsg;
            }

            ctx.OutputBytes += result.Length;
            if (ctx.OutputBytes > MaxOutputBytes) return sb.Append(ctx.ErrMsg).ToString();
            sb.Append(result);
        }
        sb.Append(content, pos, content.Length - pos);
        return sb.ToString();
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

            default:
                // #exec, #printenv, #if... are deliberately unsupported.
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
        VariableRefRegex().Replace(value, m => m.Value == "\\$" ? "$" :
            GetVariable(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, ctx, currentFile) ?? "");

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
