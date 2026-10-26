using System.Net;
using System.Text.RegularExpressions;
using Pagehaus.Ssi;

namespace Pagehaus.Cli.Check;

public enum Severity { Problem, Warning }

/// <summary>One thing wrong, and every file it's in (a broken link in a shared header is one finding, many pages).</summary>
public sealed record Finding(Severity Severity, string Message, IReadOnlyList<string> Files);

/// <summary>
/// Checks a site the way a visitor's browser would see it on a web server: pages with their includes filled in,
/// links resolved against the page, file names matched exactly (servers are case-sensitive even when your disk
/// isn't). Hidden files and folders aren't part of the site.
/// </summary>
public sealed partial class SiteChecker(SsiProcessor ssi)
{
    /// <summary>Pagehaus's Free plan limit; paid plans allow more.</summary>
    public const long LargeFileBytes = 25L * 1024 * 1024;

    [GeneratedRegex("""\b(?<attr>href|src|poster|srcset)\s*=\s*(?:"(?<v>[^"]*)"|'(?<v>[^']*)')""", RegexOptions.IgnoreCase)]
    private static partial Regex LinkAttribute();

    [GeneratedRegex("""url\(\s*(?:"(?<v>[^"]*)"|'(?<v>[^']*)'|(?<v>[^)'"\s]+))\s*\)""", RegexOptions.IgnoreCase)]
    private static partial Regex CssUrl();

    [GeneratedRegex("""<base\s[^>]*href\s*=\s*["']([^"']+)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex BaseTag();

    [GeneratedRegex("""<title>\s*\S""", RegexOptions.IgnoreCase)]
    private static partial Regex Title();

    [GeneratedRegex("""<meta\s[^>]*name\s*=\s*["']description["'][^>]*content\s*=\s*["']\s*\S|<meta\s[^>]*content\s*=\s*["']\s*\S[^>]*name\s*=\s*["']description["']""", RegexOptions.IgnoreCase)]
    private static partial Regex Description();

    [GeneratedRegex("""<(?:!doctype|html|body)\b""", RegexOptions.IgnoreCase)]
    private static partial Regex WholePage();

    [GeneratedRegex(""""<!--#(?:include|flastmod|fsize)\s+(?<kind>virtual|file)\s*=\s*"(?<v>[^"]*)"""")]
    private static partial Regex IncludeDirective();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex HtmlComment();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex CssComment();

    [GeneratedRegex("^[a-zA-Z][a-zA-Z0-9+.-]*:")]
    private static partial Regex Scheme();

    public int Pages { get; private set; }
    public int Files { get; private set; }

    public async Task<IReadOnlyList<Finding>> CheckAsync(string folder)
    {
        var root = Path.GetFullPath(folder);
        var site = SiteFiles.Read(root);
        Files = site.Files.Count;
        var found = new Dictionary<(Severity, string), List<string>>();
        void Add(Severity severity, string message, string file)
        {
            if (!found.TryGetValue((severity, message), out var files)) found[(severity, message)] = files = [];
            if (!files.Contains(file)) files.Add(file);
        }

        foreach (var rel in site.Files)
        {
            var size = new FileInfo(SitePath.ToDisk(root, rel)).Length;
            if (size > LargeFileBytes)
                Add(Severity.Warning, $"over 25 MB ({size / (1024 * 1024)} MB): too big for Pagehaus's Free plan. Put videos on a video service, or use a paid plan.", rel);

            if (rel.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
            {
                var css = CssComment().Replace(await File.ReadAllTextAsync(SitePath.ToDisk(root, rel)), "");
                foreach (Match m in CssUrl().Matches(css))
                    CheckLink(site, rel, SitePath.Parent(rel), m.Groups["v"].Value, Add);
                continue;
            }
            if (!SsiProcessor.IsSsiFile(rel)) continue;

            var raw = await File.ReadAllTextAsync(SitePath.ToDisk(root, rel));
            // Includes by exact name: on a Mac or Windows disk a wrong case still works, on the server it doesn't.
            foreach (Match m in IncludeDirective().Matches(raw))
            {
                var value = m.Groups["v"].Value;
                var cut = value.IndexOfAny(['?', '#']);
                var target = SitePath.Combine(SitePath.Parent(rel), cut < 0 ? value : value[..cut]);
                if (target is null || site.Exists(target)) continue;
                if (site.ExistsIgnoringCase(target) is { } actual)
                    Add(Severity.Problem, $"includes {value}, but the file is named {actual}. That works on Windows and Mac, not on a web server: the case must match.", rel);
            }
            var ctx = new SsiContext { SiteRoot = root, DocumentPath = rel, Host = "localhost" };
            // Includes processed first; the comments left are only comments ("replace this with <img src=...>").
            var html = HtmlComment().Replace(await ssi.ProcessAsync(raw, ctx), "");
            if (!WholePage().IsMatch(html)) continue; // a piece of a page (an include): checked as part of the pages using it
            Pages++;

            if (html.Contains("[an error occurred while processing this directive]", StringComparison.Ordinal))
                Add(Severity.Problem, "a Server Side Include failed: a file it includes is missing, or the directive has a mistake.", rel);
            var name = Path.GetFileName(rel);
            if (name is not ("404.html" or "thanks.html"))
            {
                if (!Title().IsMatch(html)) Add(Severity.Warning, "no <title>: search results and browser tabs show the address instead.", rel);
                else if (!Description().IsMatch(html))
                    Add(Severity.Warning, "no <meta name=\"description\">: search engines pick some text from the page instead.", rel);
            }

            var dir = SitePath.Parent(rel);
            if (BaseTag().Match(html) is { Success: true } b)
            {
                var href = WebUtility.HtmlDecode(b.Groups[1].Value.Trim());
                if (Scheme().IsMatch(href) || href.StartsWith("//")) continue; // links point elsewhere: nothing to check here
                dir = SitePath.Combine(dir, href.EndsWith('/') ? href : SitePath.Parent(href)) ?? dir;
            }
            foreach (Match m in LinkAttribute().Matches(html))
            {
                var value = WebUtility.HtmlDecode(m.Groups["v"].Value);
                if (m.Groups["attr"].Value.Equals("srcset", StringComparison.OrdinalIgnoreCase))
                    foreach (var candidate in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        CheckLink(site, rel, dir, candidate.Split(' ')[0], Add);
                else
                    CheckLink(site, rel, dir, value, Add);
            }
        }

        return found
            .OrderBy(f => f.Key.Item1).ThenBy(f => f.Value.Min(StringComparer.Ordinal), StringComparer.Ordinal)
            .Select(f => new Finding(f.Key.Item1, f.Key.Item2, f.Value.Order(StringComparer.Ordinal).ToList()))
            .ToList();
    }

    private static void CheckLink(SiteFiles site, string from, string dir, string link, Action<Severity, string, string> add)
    {
        link = link.Trim();
        if (link.Length == 0 || link.StartsWith('#') || link.Contains('{') || link.StartsWith("/_form/")) return;
        if (link.StartsWith("file:", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(link, @"^[a-zA-Z]:\\")
            || Regex.IsMatch(link, @"^(https?:)?//(localhost|127\.0\.0\.1)([:/]|$)", RegexOptions.IgnoreCase))
        {
            add(Severity.Problem, $"links to {link}, which is on your computer: visitors can't reach it.", from);
            return;
        }
        if (link.StartsWith("//") || Scheme().IsMatch(link)) return; // another site, mailto:, tel:, data:...

        var cut = link.IndexOfAny(['?', '#']);
        var path = cut < 0 ? link : link[..cut];
        string decoded;
        try { decoded = Uri.UnescapeDataString(path); }
        catch (UriFormatException) { decoded = path; }
        if (decoded.Contains('\\'))
        {
            add(Severity.Problem, $"links to {link}, with \\ (backslash): web addresses use /.", from);
            return;
        }
        var target = SitePath.Combine(dir, decoded);
        if (target is null)
        {
            add(Severity.Problem, $"links to {link}, which is outside the site.", from);
            return;
        }
        if (site.Exists(target)) return;
        if (site.ExistsIgnoringCase(target) is { } actual)
            add(Severity.Problem, $"links to {link}, but the file is named {actual}. That works on Windows and Mac, not on a web server: the case must match.", from);
        else
            add(Severity.Problem, $"links to {link}, which doesn't exist.", from);
    }
}

/// <summary>The site's files and folders by their exact names (site-relative), without hidden ones.</summary>
public sealed class SiteFiles
{
    private readonly HashSet<string> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _folders = new(StringComparer.Ordinal) { "" };
    private readonly Dictionary<string, string> _byLowerCase = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> Files => _files;

    public static SiteFiles Read(string root)
    {
        var site = new SiteFiles();
        foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos("*", new EnumerationOptions
                 { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true }))
        {
            var rel = Path.GetRelativePath(root, entry.FullName).Replace('\\', '/');
            if (Serve.SiteServer.IsHidden(rel)) continue;
            (entry is DirectoryInfo ? site._folders : site._files).Add(rel);
            site._byLowerCase.TryAdd(rel, rel);
        }
        return site;
    }

    /// <summary>A file, or a folder with an index page (its address shows that page).</summary>
    public bool Exists(string rel) =>
        _files.Contains(rel) || (_folders.Contains(rel) && SitePath.IndexFiles.Any(i => _files.Contains(SitePath.Join(rel, i))));

    /// <summary>The real name of a file whose name differs from <paramref name="rel"/> only in case, if there's one.</summary>
    public string? ExistsIgnoringCase(string rel)
    {
        if (_byLowerCase.TryGetValue(rel, out var actual)) return actual;
        // A folder whose name differs in case: name it with its slash.
        return SitePath.IndexFiles.Select(i => _byLowerCase.GetValueOrDefault(SitePath.Join(rel, i)))
            .FirstOrDefault(p => p is not null) is { } page ? SitePath.Parent(page) + "/" : null;
    }
}
