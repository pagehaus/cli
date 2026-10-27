using Pagehaus.Ssi;

namespace Pagehaus.Ssi.Tests;

public sealed class SsiProcessorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ph-ssi").FullName;
    private readonly SsiProcessor _ssi = new();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string rel, string content)
    {
        var p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }

    private Task<string> Run(string content, string doc = "index.html") =>
        _ssi.ProcessAsync(content, new SsiContext
        {
            SiteRoot = _root, DocumentPath = doc, Host = "demo.example", QueryString = "?q=1",
            TimeZone = TimeZoneInfo.Utc, Now = new DateTimeOffset(2026, 10, 8, 14, 5, 9, TimeSpan.Zero),
        });

    [Fact]
    public async Task Includes_virtual_paths_absolute_and_relative()
    {
        Write("inc/header.html", "<header>H</header>");
        Write("blog/side.html", "<aside>S</aside>");
        Assert.Equal("<header>H</header>|<aside>S</aside>",
            await Run("""<!--#include virtual="/inc/header.html" -->|<!--#include virtual="side.html" -->""", "blog/post.html"));
    }

    [Fact]
    public async Task Nested_includes_are_processed_and_see_variables()
    {
        Write("inc/header.html", "<title><!--#echo var=\"title\" --></title><!--#include file=\"nav.html\" -->");
        Write("inc/nav.html", "<nav>N</nav>");
        Assert.Equal("<title>Home &amp; away</title><nav>N</nav>",
            await Run("<!--#set var=\"title\" value=\"Home & away\" --><!--#include virtual=\"/inc/header.html\" -->"));
    }

    [Fact]
    public async Task Include_of_a_folder_uses_its_index()
    {
        Write("parts/index.html", "P");
        Assert.Equal("P", await Run("<!--#include virtual=\"/parts/\" -->"));
    }

    [Theory]
    [InlineData("<!--#include virtual=\"/../secret.txt\" -->")]
    [InlineData("<!--#include file=\"../secret.txt\" -->")]
    [InlineData("<!--#include file=\"/etc/passwd\" -->")]
    [InlineData("<!--#include virtual=\"/missing.html\" -->")]
    [InlineData("<!--#exec cmd=\"ls\" -->")]
    public async Task Unsafe_missing_or_unsupported_directives_show_the_error_message(string directive) =>
        Assert.Equal("[an error occurred while processing this directive]", await Run(directive, "sub/page.html"));

    [Fact]
    public async Task Recursive_includes_stop()
    {
        Write("loop.html", "x<!--#include virtual=\"/loop.html\" -->");
        var result = await Run("<!--#include virtual=\"/loop.html\" -->");
        Assert.Equal(SsiProcessor.MaxDepth, result.Count(c => c == 'x'));
    }

    [Fact]
    public async Task Echo_builtin_variables_and_timefmt()
    {
        Assert.Equal("index.html /blog/index.html demo.example q=1",
            await Run("<!--#echo var=\"DOCUMENT_NAME\" --> <!--#echo var=\"DOCUMENT_URI\" --> <!--#echo var=\"HTTP_HOST\" --> <!--#echo var=\"QUERY_STRING\" -->", "blog/index.html"));
        Assert.Equal("2026-10-08 14:05", await Run("<!--#config timefmt=\"%Y-%m-%d %H:%M\" --><!--#echo var=\"DATE_GMT\" -->"));
        Assert.Equal("(none)", await Run("<!--#echo var=\"nope\" -->"));
    }

    [Fact]
    public async Task Set_substitutes_variables_and_config_errmsg_applies()
    {
        Assert.Equal("Hi demo.example", await Run("<!--#set var=\"x\" value=\"Hi $HTTP_HOST\" --><!--#echo var=\"x\" -->"));
        Assert.Equal("oops", await Run("<!--#config errmsg=\"oops\" --><!--#include virtual=\"/nope\" -->"));
    }

    [Fact]
    public async Task Config_echomsg_sets_what_undefined_variables_print()
    {
        const string headLine = "<!--#config echomsg=\"\" --><!--#echo var=\"blog_head\" encoding=\"none\" -->";
        Assert.Equal("<head></head>", await Run($"<head>{headLine}</head>"));
        Assert.Equal("<head><link rel=\"alternate\" href=\"/blog/feed.xml\"></head>",
            await Run($"<!--#set var='blog_head' value='<link rel=\"alternate\" href=\"/blog/feed.xml\">' --><head>{headLine}</head>"));
        Assert.Equal("?", await Run("<!--#config echomsg=\"?\" --><!--#echo var=\"nope\" -->"));
    }

    [Fact]
    public async Task Backslashes_escape_quotes_and_dollar_signs_in_values()
    {
        Assert.Equal("Say &quot;hi&quot; for $5", await Run("<!--#set var=\"x\" value=\"Say \\\"hi\\\" for \\$5\" --><!--#echo var=\"x\" -->"));
        Assert.Equal("It&#39;s", await Run("<!--#set var='x' value='It\\'s' --><!--#echo var=\"x\" -->"));
        Assert.Equal("a\\b", await Run("<!--#set var=\"x\" value=\"a\\\\b\" --><!--#echo var=\"x\" encoding=\"none\" -->"));
        Assert.Equal("C:\\temp", await Run("<!--#set var=\"x\" value=\"C:\\temp\" --><!--#echo var=\"x\" encoding=\"none\" -->"));
    }

    [Fact]
    public async Task Fsize_reports_size()
    {
        Write("big.txt", new string('a', 2048));
        Assert.Equal("2K", await Run("<!--#fsize virtual=\"/big.txt\" -->"));
        Assert.Equal("2,048", await Run("<!--#config sizefmt=\"bytes\" --><!--#fsize file=\"big.txt\" -->"));
    }

    [Fact]
    public async Task Text_without_directives_is_untouched()
    {
        const string html = "<p>Hello <!-- normal comment --></p>";
        Assert.Equal(html, await Run(html));
    }

    [Fact]
    public async Task Symbolic_links_are_refused_unless_followed_on_purpose()
    {
        var outside = Directory.CreateTempSubdirectory("ph-ssi-outside").FullName;
        try
        {
            File.WriteAllText(Path.Combine(outside, "secret.html"), "SECRET");
            File.CreateSymbolicLink(Path.Combine(_root, "linked.html"), Path.Combine(outside, "secret.html"));
            Directory.CreateSymbolicLink(Path.Combine(_root, "shared"), outside);
            const string page = """<!--#include virtual="/linked.html" -->|<!--#include virtual="/shared/secret.html" -->|<!--#fsize virtual="/linked.html" -->""";

            var error = "[an error occurred while processing this directive]";
            Assert.Equal($"{error}|{error}|{error}", await Run(page));

            var followed = await _ssi.ProcessAsync(page, new SsiContext { SiteRoot = _root, DocumentPath = "index.html", FollowLinks = true });
            Assert.Equal("SECRET|SECRET|6", followed);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    private async Task<(string Html, int Lookups)> Count(string content, long total, string doc = "index.html")
    {
        var lookups = 0;
        var html = await _ssi.ProcessAsync(content, new SsiContext
        {
            SiteRoot = _root, DocumentPath = doc,
            VisitorCount = () => { lookups++; return ValueTask.FromResult(total); },
        });
        return (html, lookups);
    }

    [Theory]
    [InlineData("<!--#counter -->", 7, "7")]
    [InlineData("<!--#counter -->", 12345, "12,345")]
    [InlineData("<!--#counter -->", 1234567, "1,234,567")]
    [InlineData("<!--#counter start=\"1000\" -->", 234, "1,234")]
    [InlineData("<!--#counter separator=\"\" -->", 12345, "12345")]
    [InlineData("<!--#counter separator=\".\" -->", 1234567, "1.234.567")]
    [InlineData("<!--#counter separator=\" \" start=\"1000000000\" -->", 0, "1 000 000 000")]
    [InlineData("<!--#counter separator=\"<\" -->", 1000, "1&lt;000")]
    public async Task Counter_shows_the_total_with_separators(string directive, long total, string expected) =>
        Assert.Equal(expected, (await Count(directive, total)).Html);

    [Theory]
    [InlineData("<!--#counter start=\"lots\" -->")]
    [InlineData("<!--#counter start=\"-5\" -->")]
    [InlineData("<!--#counter start=\"1000000001\" -->")]
    [InlineData("<!--#counter start=\"1e6\" -->")]
    [InlineData("<!--#counter separator=\"::\" -->")]
    [InlineData("<!--#counter theme=\"disco\" -->")]
    [InlineData("<!--#counter color=\"red;background:url(x)\" -->")]
    public async Task Counter_with_a_bad_attribute_shows_the_error_message(string directive) =>
        Assert.Equal("[an error occurred while processing this directive]", (await Count(directive, 5)).Html);

    [Fact]
    public async Task Counter_without_a_total_shows_the_error_message() =>
        Assert.Equal("[an error occurred while processing this directive]", await Run("<!--#counter -->"));

    [Fact]
    public async Task Counters_in_the_page_and_its_includes_ask_for_the_total_once()
    {
        Write("inc/footer.html", "<footer><!--#counter --></footer>");
        var (html, lookups) = await Count("<p><!--#counter start=\"100\" --></p><!--#include virtual=\"/inc/footer.html\" -->", 41);
        Assert.Equal("<p>141</p><footer>41</footer>", html);
        Assert.Equal(1, lookups);
    }
}
