using Pagehaus.Ssi;

namespace Pagehaus.Ssi.Tests;

public sealed class SsiConditionTests : IDisposable
{
    private const string Err = "[an error occurred while processing this directive]";
    private readonly string _root = Directory.CreateTempSubdirectory("ph-ssi-if").FullName;
    private readonly SsiProcessor _ssi = new();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string rel, string content)
    {
        var p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }

    private Task<string> Run(string content, string doc = "about.html", string? request = null, string query = "") =>
        _ssi.ProcessAsync(content, new SsiContext
        {
            SiteRoot = _root, DocumentPath = doc, RequestPath = request, QueryString = query, Host = "demo.example",
        });

    private async Task<bool> True(string expr, string doc = "about.html")
    {
        var result = await Run($"""<!--#if expr="{expr}" -->yes<!--#else -->no<!--#endif -->""", doc);
        return result switch
        {
            "yes" => true,
            "no" => false,
            _ => throw new Xunit.Sdk.XunitException($"{expr}: {result}"),
        };
    }

    [Fact]
    public async Task Marks_the_current_page_in_a_shared_menu()
    {
        Write("includes/nav.html", """
            <a href="/"<!--#if expr="$DOCUMENT_URI = /index.html" --> aria-current="page"<!--#endif -->>Home</a>
            <a href="/about.html"<!--#if expr="$DOCUMENT_URI = /about.html" --> aria-current="page"<!--#endif -->>About</a>
            """);
        const string page = """<!--#include virtual="/includes/nav.html" -->""";
        Assert.Equal("""
            <a href="/">Home</a>
            <a href="/about.html" aria-current="page">About</a>
            """, await Run(page, "about.html"));
        Assert.Equal("""
            <a href="/" aria-current="page">Home</a>
            <a href="/about.html">About</a>
            """, await Run(page, "index.html"));
    }

    [Fact]
    public async Task Comparisons()
    {
        Assert.True(await True("$DOCUMENT_NAME = about.html"));
        Assert.True(await True("$DOCUMENT_NAME == about.html"));
        Assert.False(await True("$DOCUMENT_NAME = About.html")); // case matters
        Assert.True(await True("$DOCUMENT_NAME != index.html"));
        Assert.True(await True("abc < abd"));
        Assert.True(await True("abc <= abc"));
        Assert.True(await True("b > a"));
        Assert.True(await True("b >= b"));
        Assert.False(await True("10 > 9")); // as text, as in Apache
    }

    [Fact]
    public async Task Strings_alone_are_true_when_not_empty()
    {
        Assert.True(await True("$DOCUMENT_NAME"));
        Assert.False(await True("$NOT_SET"));
        Assert.False(await True("''"));
        Assert.True(await True("!$NOT_SET"));
    }

    [Fact]
    public async Task Variables_quotes_and_joined_words()
    {
        Assert.Equal("yes", await Run("""<!--#set var="TITLE" value="About us" --><!--#if expr="$TITLE = 'About us'" -->yes<!--#endif -->"""));
        Assert.Equal("yes", await Run("""<!--#set var="A" value="x" --><!--#if expr="${A}y = xy" -->yes<!--#endif -->"""));
        // Words side by side are joined with a space, as in Apache.
        Assert.Equal("yes", await Run("""<!--#set var="A" value="About" --><!--#if expr="$A us = 'About us'" -->yes<!--#endif -->"""));
        Assert.Equal("yes", await Run("""<!--#if expr="'it\'s' = 'it\'s'" -->yes<!--#endif -->"""));
        Assert.True(await True(@"'\$5' = '\$5'"));
    }

    [Fact]
    public async Task And_or_not_and_brackets()
    {
        Assert.True(await True("$DOCUMENT_NAME = about.html && $HTTP_HOST = demo.example"));
        Assert.False(await True("$DOCUMENT_NAME = about.html && $HTTP_HOST = other"));
        Assert.True(await True("$DOCUMENT_NAME = x || $HTTP_HOST = demo.example"));
        Assert.True(await True("!($DOCUMENT_NAME = x)"));
        Assert.True(await True("!$DOCUMENT_NAME = x")); // the comparison first: !(a = x)
        Assert.True(await True("($DOCUMENT_NAME = x || a = a) && b = b"));
    }

    [Fact]
    public async Task Patterns_match_and_their_groups_can_be_shown()
    {
        Assert.True(await True(@"$DOCUMENT_URI = /^\/blog\//", "blog/post.html"));
        Assert.False(await True(@"$DOCUMENT_URI = /^\/blog\//", "about.html"));
        Assert.True(await True(@"$DOCUMENT_URI != /^\/blog\//", "about.html"));
        Assert.True(await True("$DOCUMENT_NAME = /(about|contact) page|about/"));
        // A path with no closing slash is plain text.
        Assert.True(await True("$DOCUMENT_URI = /about.html"));
        Assert.Equal("post-1",
            await Run("""<!--#if expr="$DOCUMENT_URI = /^\/blog\/(.+)\.html$/" --><!--#echo var="1" --><!--#endif -->""", "blog/post-1.html"));
    }

    [Fact]
    public async Task Patterns_the_fast_engine_cant_run_still_work()
    {
        Assert.True(await True(@"$DOCUMENT_NAME = /^(a)bout\.\w+$/"));
        Assert.True(await True(@"$DOCUMENT_NAME = /^(?=about)/")); // lookahead
    }

    [Fact]
    public async Task Elif_and_else_take_the_first_true_branch()
    {
        const string notice = """
            <!--#if expr="$QUERY_STRING = sent" -->Thanks<!--#elif expr="$QUERY_STRING = error" -->Sorry<!--#elif expr="$QUERY_STRING = /e/" -->E<!--#else -->Write to us<!--#endif -->
            """;
        Assert.Equal("Thanks", await Run(notice, query: "?sent"));
        Assert.Equal("Sorry", await Run(notice, query: "?error"));
        Assert.Equal("E", await Run(notice, query: "?e"));
        Assert.Equal("Write to us", await Run(notice));
    }

    [Fact]
    public async Task Conditions_nest()
    {
        const string page = """<!--#if expr="$A" -->a<!--#if expr="$B" -->b<!--#else -->!b<!--#endif -->.<!--#else -->!a<!--#if expr="$B" -->b<!--#endif --><!--#endif -->""";
        Assert.Equal("ab.", await Run("""<!--#set var="A" value="1" --><!--#set var="B" value="1" -->""" + page));
        Assert.Equal("a!b.", await Run("""<!--#set var="A" value="1" -->""" + page));
        Assert.Equal("!a", await Run(page));
    }

    [Fact]
    public async Task Nothing_in_a_hidden_branch_runs()
    {
        Assert.Equal("kept", await Run("""<!--#if expr="$NO" --><!--#include virtual="/missing.html" --><!--#set var="X" value="set" --><!--#endif --><!--#if expr="$X" -->set<!--#else -->kept<!--#endif -->"""));
        // Mistakes inside a hidden branch don't show either, nested ones included.
        Assert.Equal("", await Run("""<!--#if expr="$NO" --><!--#if expr="v('x')" -->x<!--#else -->a<!--#else -->b<!--#endif --><!--#endif -->"""));
    }

    [Fact]
    public async Task Request_uri_is_the_address_asked_for()
    {
        Assert.Equal("/blog/", await Run("""<!--#echo var="REQUEST_URI" -->""", "blog/index.html", request: "/blog/"));
        Assert.Equal("/blog/index.html", await Run("""<!--#echo var="DOCUMENT_URI" -->""", "blog/index.html", request: "/blog/"));
        Assert.Equal("/about.html", await Run("""<!--#echo var="REQUEST_URI" -->""")); // without one, the page's own
        Assert.Equal("yes", await Run("""<!--#if expr="$REQUEST_URI = /^\/blog\//" -->yes<!--#endif -->""", "blog/index.html", request: "/blog/"));
    }

    [Theory]
    [InlineData("""<!--#if expr="v('DOCUMENT_URI') == '/about.html'" -->x<!--#endif -->""")]
    [InlineData("""<!--#if expr="%{DOCUMENT_URI} == /about.html" -->x<!--#endif -->""")]
    [InlineData("""<!--#if expr="-z $NO" -->x<!--#endif -->""")]
    [InlineData("""<!--#if expr="$DOCUMENT_URI =~ /about/" -->x<!--#endif -->""")]
    [InlineData("""<!--#if expr="a = " -->x<!--#endif -->""")]
    [InlineData("""<!--#if expr="(a = a" -->x<!--#endif -->""")]
    [InlineData("""<!--#if expr="'open" -->x<!--#endif -->""")]
    [InlineData("""<!--#if expr="a & b" -->x<!--#endif -->""")]
    [InlineData("""<!--#if expr="a < /b/" -->x<!--#endif -->""")]
    [InlineData("""<!--#if expr="a = /(/" -->x<!--#endif -->""")]
    [InlineData("""<!--#if expr="" -->x<!--#endif -->""")]
    [InlineData("""<!--#if -->x<!--#endif -->""")]
    public async Task An_expression_that_cant_be_read_is_an_error_and_shows_no_branch(string page)
    {
        Assert.Equal(Err, await Run(page));
        Assert.Equal(Err, await Run(page.Replace("<!--#endif -->", "<!--#else -->y<!--#endif -->")));
    }

    [Theory]
    [InlineData("""a<!--#endif -->b""", "a" + Err + "b")]
    [InlineData("""a<!--#else -->b""", "a" + Err + "b")]
    [InlineData("""a<!--#elif expr="x" -->b""", "a" + Err + "b")]
    [InlineData("""<!--#if expr="x" -->a<!--#else -->b<!--#elif expr="y" -->c<!--#endif -->d""", "a" + Err + "d")]
    [InlineData("""<!--#if expr="x" -->a<!--#else -->b<!--#else -->c<!--#endif -->d""", "a" + Err + "d")]
    [InlineData("""<!--#if expr="x" -->a""", "a" + Err)]
    [InlineData("""<!--#if expr="" -->a""", Err + Err)]
    public async Task Unbalanced_conditions_are_errors(string page, string expected) => Assert.Equal(expected, await Run(page));

    [Fact]
    public async Task An_unclosed_if_in_an_include_doesnt_hide_the_page()
    {
        Write("includes/header.html", """<!--#if expr="$NO" -->hidden""");
        Assert.Equal("<h>" + Err + "</h>after", await Run("""<h><!--#include virtual="/includes/header.html" --></h>after"""));
    }

    [Fact]
    public async Task Conditions_nest_only_so_deep()
    {
        var deep = string.Concat(Enumerable.Repeat("""<!--#if expr="a" -->""", SsiProcessor.MaxConditionDepth)) + "in" +
                   string.Concat(Enumerable.Repeat("<!--#endif -->", SsiProcessor.MaxConditionDepth));
        Assert.Equal("in", await Run(deep));
        Assert.Equal(Err, await Run("""<!--#if expr="a" -->""" + deep + "<!--#endif -->"));
    }

    [Fact]
    public async Task Long_expressions_and_patterns_are_refused()
    {
        Assert.Equal(Err, await Run($"""<!--#if expr="{new string('a', SsiExpression.MaxLength + 1)}" -->x<!--#endif -->"""));
        Assert.Equal(Err, await Run($"""<!--#if expr="a = /{new string('a', SsiExpression.MaxPatternLength + 1)}/" -->x<!--#endif -->"""));
    }

    [Fact]
    public async Task A_pattern_that_would_run_for_ever_finishes()
    {
        // Catastrophic with backtracking; the backreference keeps it off the fast engine, so the time limit applies.
        var started = DateTime.UtcNow;
        Assert.True(await True("""$X != /^(a+)+\1b$/""".Replace("$X", new string('a', 40))));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Lint_finds_mistakes_and_missing_files_in_every_branch()
    {
        Write("includes/real.html", "r");
        const string file = """
            <!--#include virtual="/not-checked-here.html" -->
            <!--#if expr="$DOCUMENT_URI = /a.html" -->
              <!--#include virtual="/includes/real.html" -->
            <!--#elif expr="v('x')" -->
              <!--#include virtual="/includes/missing.html" -->
            <!--#else -->
            <!--#else -->
            <!--#endif -->
            <!--#endif -->
            <!--#if expr="a" -->
            """;
        var problems = SsiProcessor.Lint(file, _root, "index.html");
        Assert.Equal([4, 5, 7, 9, 10], problems.Select(p => p.Line));
        Assert.Contains("newer syntax", problems[0].Message);
        Assert.Contains("/includes/missing.html", problems[1].Message);
        Assert.Contains("second #else", problems[2].Message);
        Assert.Contains("#endif without", problems[3].Message);
        Assert.Contains("without an #endif", problems[4].Message);
        Assert.Empty(SsiProcessor.Lint("""<!--#if expr="a" --><!--#include file="includes/real.html" --><!--#endif -->""", _root, "index.html"));
    }
}
