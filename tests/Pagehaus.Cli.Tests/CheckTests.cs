using Pagehaus.Cli.Check;
using Pagehaus.Ssi;

namespace Pagehaus.Cli.Tests;

/// <summary>pagehaus check, on a site with one of everything that can go wrong.</summary>
public sealed class CheckTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pagehaus-check").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string rel, string content)
    {
        var path = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string Page(string body, string title = "Hi") =>
        $"""<!doctype html><html><head><title>{title}</title><meta name="description" content="A page"></head><body>{body}</body></html>""";

    private async Task<IReadOnlyList<Finding>> CheckAsync() => await new SiteChecker(new SsiProcessor()).CheckAsync(_root);

    private static Finding Single(IReadOnlyList<Finding> findings, string text) =>
        Assert.Single(findings, f => f.Message.Contains(text, StringComparison.Ordinal));

    [Fact]
    public async Task A_healthy_site_has_nothing_to_report()
    {
        Write("index.html", Page("""
            <a href="about.html">About</a> <a href="blog/">Blog</a> <a href="/blog">Blog</a>
            <img src="img/me.jpg" srcset="img/me.jpg 1x, img/me%402x.jpg 2x"> <a href="#top">Top</a>
            <a href="https://example.com/">Elsewhere</a> <a href="mailto:a@example.com">Mail</a> <a href="?page=2">Next</a>
            <form action="/_form/contact"></form>
            """));
        Write("about.html", Page("""<a href="./">Home</a> <link href="style.css" rel="stylesheet">"""));
        Write("blog/index.html", Page("""<a href="../index.html#x">Home</a>"""));
        Write("style.css", """body { background: url("img/me.jpg") } h1 { background: url(img/me@2x.jpg) } /* url(old.png) */""");
        Write("contact.html", Page("""<!-- Add a map: <img src="map.png"> -->"""));
        Write("img/me.jpg", "jpg");
        Write("img/me@2x.jpg", "jpg");
        Write(".git/config", "hidden, and not part of the site");

        Assert.Empty(await CheckAsync());
    }

    [Fact]
    public async Task Broken_links_are_reported_once_with_every_page_they_are_on()
    {
        Write("includes/header.html", """<nav><a href="/contact.html">Contact</a></nav>""");
        foreach (var page in new[] { "index.html", "about.html", "blog/post.html" })
            Write(page, Page("""<!--#include virtual="/includes/header.html" --><img src="logo.png">"""));

        var findings = await CheckAsync();
        var contact = Single(findings, "links to /contact.html, which doesn't exist");
        Assert.Equal(Severity.Problem, contact.Severity);
        Assert.Equal(["about.html", "blog/post.html", "index.html"], contact.Files.Order());
        // Missing from each page's own folder: one finding, as written in the pages.
        Assert.Equal(["about.html", "blog/post.html", "index.html"], Single(findings, "links to logo.png").Files.Order());
    }

    [Fact]
    public async Task Names_must_match_exactly_even_where_the_disk_does_not_care()
    {
        Write("img/Team.JPG", "jpg");
        Write("Includes/footer.html", "<footer>F</footer>");
        Write("index.html", Page("""<img src="img/team.jpg"><a href="IMG/Team.JPG">x</a><!--#include virtual="/includes/footer.html" -->"""));

        var findings = await CheckAsync();
        Assert.Contains("the file is named img/Team.JPG", Single(findings, "links to img/team.jpg").Message);
        Single(findings, "links to IMG/Team.JPG");
        Assert.Contains("the file is named Includes/footer.html", Single(findings, "includes /includes/footer.html").Message);
    }

    [Fact]
    public async Task Links_to_your_own_computer_and_failed_includes_are_problems()
    {
        Write("index.html", Page("""
            <img src="file:///C:/Users/me/photo.jpg"><a href="C:\site\about.html">x</a>
            <a href="http://localhost:8080/">x</a><a href="images\a.png">x</a><a href="../../up.html">x</a>
            <!--#include virtual="/missing.html" -->
            """));

        var findings = await CheckAsync();
        Assert.All(findings, f => Assert.Equal(Severity.Problem, f.Severity));
        Single(findings, "file:///C:/Users/me/photo.jpg, which is on your computer");
        Single(findings, @"C:\site\about.html, which is on your computer");
        Single(findings, "http://localhost:8080/, which is on your computer");
        Single(findings, "with \\ (backslash)");
        Single(findings, "outside the site");
        Single(findings, "a Server Side Include failed");
    }

    [Fact]
    public async Task Big_files_and_pages_without_a_title_or_description_are_warnings()
    {
        Write("index.html", "<!doctype html><html><head></head><body>No title</body></html>");
        Write("about.html", "<!doctype html><html><head><title>About</title></head><body></body></html>");
        Write("404.html", "<!doctype html><html><body>Not found pages don't need one</body></html>");
        Write("includes/part.html", "<p>A piece of a page, not a page</p>");
        using (var big = File.Create(Path.Combine(_root, "video.mp4"))) big.SetLength(SiteChecker.LargeFileBytes + 1);

        var findings = await CheckAsync();
        Assert.All(findings, f => Assert.Equal(Severity.Warning, f.Severity));
        Assert.Equal(["index.html"], Single(findings, "no <title>").Files);
        Assert.Equal(["about.html"], Single(findings, "no <meta name=\"description\">").Files);
        Assert.Equal(["video.mp4"], Single(findings, "over 25 MB").Files);
        Assert.Equal(3, findings.Count);
    }

    [Fact]
    public async Task The_command_fails_only_for_problems_unless_strict()
    {
        Write("index.html", "<!doctype html><html><body>no title</body></html>");
        var output = new StringWriter();
        Assert.Equal(0, await CheckCommand.RunAsync(_root, strict: false, output));
        Assert.Contains("warning: index.html: no <title>", output.ToString());
        Assert.Contains("0 problems, 1 warning.", output.ToString());
        Assert.Equal(1, await CheckCommand.RunAsync(_root, strict: true, new StringWriter()));

        Write("about.html", Page("""<a href="gone.html">x</a>"""));
        Assert.Equal(1, await CheckCommand.RunAsync(_root, strict: false, new StringWriter()));
    }
}
