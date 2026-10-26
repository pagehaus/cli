namespace Pagehaus.Ssi;

/// <summary>
/// Site-relative paths use '/' separators, no leading slash, and "" for the site root.
/// Everything that turns user input into a disk path goes through here.
/// </summary>
public static class SitePath
{
    /// <summary>
    /// Resolves <paramref name="target"/> against the site-relative directory <paramref name="baseDir"/>.
    /// A leading '/' in target means the site root. Returns null if the path would escape the root
    /// or contains characters we never allow.
    /// </summary>
    public static string? Combine(string baseDir, string? target)
    {
        target = (target ?? "").Replace('\\', '/');
        var parts = new List<string>();
        if (!target.StartsWith('/'))
            parts.AddRange(baseDir.Split('/', StringSplitOptions.RemoveEmptyEntries));

        foreach (var seg in target.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".") continue;
            if (seg == "..")
            {
                if (parts.Count == 0) return null;
                parts.RemoveAt(parts.Count - 1);
                continue;
            }
            if (!IsValidName(seg)) return null;
            parts.Add(seg);
        }
        return string.Join('/', parts);
    }

    public static string? Normalize(string? path) => Combine("", "/" + path);

    public static bool IsValidName(string name) =>
        name.Length is > 0 and <= 255
        && name != "." && name != ".."
        && !name.Any(c => c < 32 || c == '/' || c == '\\' || c == 127);

    /// <summary>Maps a normalized site-relative path to an absolute disk path inside <paramref name="root"/>.</summary>
    public static string ToDisk(string root, string relative)
    {
        var rootFull = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(rootFull, relative));
        if (full != rootFull && !full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Path escapes site root.");
        return full;
    }

    /// <summary>
    /// Whether a folder or file on the way from <paramref name="root"/> to <paramref name="relative"/>, the file itself
    /// included, is a symbolic link. One lstat per part of the path.
    /// </summary>
    public static bool HasLink(string root, string relative)
    {
        var path = Path.GetFullPath(root);
        foreach (var seg in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            path = Path.Combine(path, seg);
            if (new FileInfo(path).LinkTarget is not null) return true;
        }
        return false;
    }

    public static string Parent(string relative)
    {
        var i = relative.LastIndexOf('/');
        return i < 0 ? "" : relative[..i];
    }

    public static string Join(string dir, string name) => dir.Length == 0 ? name : dir + "/" + name;

    /// <summary>The pages a folder's address shows, in order of preference.</summary>
    public static readonly IReadOnlyList<string> IndexFiles = ["index.html", "index.shtml", "index.htm"];

    /// <summary>The index page of the site-relative folder <paramref name="dir"/>, or null if it has none.</summary>
    public static string? FindIndex(string siteRoot, string dir) =>
        IndexFiles.Select(f => Join(dir, f)).FirstOrDefault(p => File.Exists(ToDisk(siteRoot, p)));
}
