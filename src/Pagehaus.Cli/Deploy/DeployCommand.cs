using System.IO.Compression;
using Pagehaus.Cli.Check;
using Pagehaus.Ssi;

namespace Pagehaus.Cli.Deploy;

/// <summary>
/// pagehaus deploy: checks the folder, zips it (without hidden files) and sends it to a Pagehaus site, where it's
/// unpacked over what's there: new and changed files are written, files you deleted locally stay on the site.
/// </summary>
public static class DeployCommand
{
    private const int FilesListed = 15;

    public static async Task<int> RunAsync(string folder, string? site, bool dryRun, bool force, TextWriter output)
    {
        if (!Directory.Exists(folder)) throw new CliException($"There's no folder \"{folder}\".");
        var token = Config.CurrentToken()
                    ?? throw new CliException($"Not logged in: run pagehaus login (or set {Config.TokenVariable}).");
        using var api = PagehausApi.Create(token);
        site ??= await ChooseSiteAsync(api);

        var checker = new SiteChecker(new SsiProcessor());
        var problems = (await checker.CheckAsync(folder)).Where(f => f.Severity == Severity.Problem).ToList();
        if (problems.Count > 0 && !force)
        {
            foreach (var p in problems)
                output.WriteLine($"problem: {string.Join(", ", p.Files.Take(3))}{(p.Files.Count > 3 ? $" and {p.Files.Count - 3} more" : "")}: {p.Message}");
            throw new CliException($"Not deployed: pagehaus check found {problems.Count} problem{(problems.Count == 1 ? "" : "s")}. " +
                                   "Fix them, or deploy anyway with --force.");
        }

        var root = Path.GetFullPath(folder);
        var files = SiteFiles.Read(root).Files.Order(StringComparer.Ordinal).ToList();
        if (files.Count == 0) throw new CliException($"{root} has no files to deploy.");
        var zipPath = Path.GetTempFileName();
        try
        {
            await using (var zipFile = File.Create(zipPath))
            using (var zip = new ZipArchive(zipFile, ZipArchiveMode.Create))
                foreach (var rel in files)
                    zip.CreateEntryFromFile(SitePath.ToDisk(root, rel), rel, CompressionLevel.Optimal);

            var size = new FileInfo(zipPath).Length;
            output.WriteLine($"{(dryRun ? "Checking what would change on" : "Deploying to")} {site}: " +
                             $"{files.Count} file{(files.Count == 1 ? "" : "s")}, {Bytes(size)} zipped...");
            await using var upload = File.OpenRead(zipPath);
            var result = await api.UploadZipAsync(site, upload, dryRun);
            Report(result, output);
            if (!dryRun && result.Extracted.Count + result.Replaced.Count > 0)
                output.WriteLine($"Done: {(await SiteUrlAsync(api, site)) ?? site}");
            return result.Removed.Count > 0 ? 1 : 0;
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    /// <summary>The site a token can deploy to when it's the only one; otherwise --site is needed.</summary>
    private static async Task<string> ChooseSiteAsync(PagehausApi api)
    {
        var me = await api.MeAsync();
        if (me.Site is { } only) return only;
        var sites = (await api.SitesAsync()).Where(s => !s.Suspended).ToList();
        return sites.Count switch
        {
            1 => sites[0].Name,
            0 => throw new CliException("Your account has no sites yet: make one at " + Config.Url + "/sites."),
            _ => throw new CliException($"Which site? Add --site with one of: {string.Join(", ", sites.Select(s => s.Name))}."),
        };
    }

    private static async Task<string?> SiteUrlAsync(PagehausApi api, string site)
    {
        try
        {
            return (await api.SitesAsync()).FirstOrDefault(s => s.Name == site)?.Url;
        }
        catch (CliException)
        {
            return null; // a token for one site may not list sites: the name is enough
        }
    }

    public static void Report(ZipResult result, TextWriter output)
    {
        // Extracted is every file written; Replaced, those that were already there (whatever their contents).
        var verb = result.DryRun ? "would be" : "";
        List("new", result.Extracted.Except(result.Replaced).ToList());
        List("replaced", result.Replaced);
        foreach (var r in result.Removed) output.WriteLine($"  not uploaded: {r.Path}: {r.Reason}");
        if (result.Extracted.Count + result.Replaced.Count == 0 && result.Removed.Count == 0)
            output.WriteLine("  Nothing to upload: the site already has these files.");
        if (result.DryRun) output.WriteLine("Dry run: nothing was changed. Run without --dry-run to deploy.");

        void List(string what, List<string> paths)
        {
            if (paths.Count == 0) return;
            output.WriteLine($"  {paths.Count} {what}{(verb.Length > 0 ? $" ({verb} written)" : "")}: " +
                             string.Join(", ", paths.Take(FilesListed)) + (paths.Count > FilesListed ? $" and {paths.Count - FilesListed} more" : ""));
        }
    }

    private static string Bytes(long n) => n switch
    {
        < 1024 => $"{n} bytes",
        < 1024 * 1024 => $"{n / 1024.0:0.#} KB",
        _ => $"{n / (1024.0 * 1024):0.#} MB",
    };
}
