namespace Pagehaus.Ssi.Tests;

/// <summary>_redirects files: what they match, where they send visitors, and the lines they skip.</summary>
public sealed class RedirectRulesTests
{
    private static Redirect? Go(string rules, string path, string? query = null) => RedirectRules.Parse(rules).Match(path, query);

    private static string Problem(string rules) => Assert.Single(RedirectRules.Parse(rules).Problems).Message;

    [Fact]
    public void A_rule_is_301_unless_it_says_302()
    {
        Assert.Equal(new Redirect("/about.html", 301), Go("/about-us /about.html", "/about-us"));
        Assert.Equal(new Redirect("/about.html", 301), Go("/about-us /about.html 301", "/about-us"));
        Assert.Equal(new Redirect("/sale.html", 302), Go("/offer /sale.html 302", "/offer"));
    }

    [Fact]
    public void Comments_blank_lines_tabs_and_runs_of_spaces_are_fine()
    {
        var rules = RedirectRules.Parse("# moved pages\n\n/a\t\t/b\r\n   /c      /d     302   \n");
        Assert.Empty(rules.Problems);
        Assert.Equal(2, rules.Count);
        Assert.Equal(new Redirect("/d", 302), rules.Match("/c"));
    }

    [Fact]
    public void Matching_ignores_case_and_a_trailing_slash()
    {
        const string rules = "/About-Us /about.html\n/team/ /people.html";
        Assert.Equal("/about.html", Go(rules, "/about-us/")?.Location);
        Assert.Equal("/about.html", Go(rules, "/ABOUT-US")?.Location);
        Assert.Equal("/people.html", Go(rules, "/team")?.Location);
        Assert.Null(Go(rules, "/about-us/more"));
        Assert.Null(Go(rules, "/about"));
    }

    [Fact]
    public void The_first_match_wins()
    {
        Assert.Equal("/first", Go("/a /first\n/a /second\n/* /third", "/a")?.Location);
        Assert.Equal("/third", Go("/a /first\n/* /third", "/b")?.Location);
    }

    [Fact]
    public void A_star_matches_the_rest_and_splat_puts_it_in_the_new_address()
    {
        const string rules = "/blog/* /posts/:splat";
        Assert.Equal("/posts/2019/05/hello", Go(rules, "/blog/2019/05/hello")?.Location);
        Assert.Equal("/posts/", Go(rules, "/blog/")?.Location);
        Assert.Equal("/posts/", Go(rules, "/blog")?.Location);
        Assert.Null(Go(rules, "/blogs/x"));
        Assert.Equal("https://new.example/old/a/b", Go("/* https://new.example/old/:splat", "/a/b")?.Location);
    }

    [Fact]
    public void Named_parts_match_one_segment_and_keep_their_case()
    {
        const string rules = "/news/:year/:slug /posts/:year-:slug.html";
        Assert.Equal("/posts/2019-My-Post.html", Go(rules, "/news/2019/My-Post")?.Location);
        Assert.Null(Go(rules, "/news/2019"));
        Assert.Null(Go(rules, "/news/2019/a/b"));
    }

    [Fact]
    public void The_query_string_is_passed_on_unless_the_new_address_has_its_own()
    {
        Assert.Equal("/b?ref=mail", Go("/a /b", "/a", "?ref=mail")?.Location);
        Assert.Equal("/b?x=1", Go("/a /b?x=1", "/a", "?ref=mail")?.Location);
        Assert.Equal("/b?ref=mail#top", Go("/a /b#top", "/a", "?ref=mail")?.Location);
        Assert.Equal("/b", Go("/a /b", "/a", "?")?.Location);
        Assert.Equal("/b", Go("/a /b", "/a", "")?.Location);
    }

    [Fact]
    public void Encoded_and_non_ascii_addresses_match_and_come_out_encoded()
    {
        Assert.Equal("/caf%C3%A9.html", Go("/café /café.html", "/CAFÉ")?.Location);
        Assert.Equal("/caf%C3%A9.html", Go("/caf%C3%A9 /caf%C3%A9.html", "/café")?.Location);
        Assert.Equal("/posts/a%20b/c%3Fd", Go("/blog/* /posts/:splat", "/blog/a b/c?d")?.Location);
        Assert.Equal("/p/x%2Fy", Go("/:id /p/:id", "/x%2Fy")?.Location);
    }

    [Fact]
    public void A_location_never_carries_a_newline_or_a_space_whatever_comes_in()
    {
        var hit = Go("/* /x/:splat", "/a\r\nSet-Cookie: x=1", "?q=\r\nevil")!;
        Assert.DoesNotContain('\r', hit.Location);
        Assert.DoesNotContain('\n', hit.Location);
        Assert.DoesNotContain(' ', hit.Location);
        Assert.All(hit.Location, c => Assert.InRange(c, '!', '~'));
    }

    [Fact]
    public void Other_websites_are_allowed_unless_the_host_refuses_them()
    {
        Assert.Equal(new Redirect("https://shop.example.com/", 301), Go("/shop https://shop.example.com/", "/shop"));
        Assert.Equal("http://old.example/x", Go("/x http://old.example/x", "/x")?.Location);

        var refused = RedirectRules.Parse("/shop https://shop.example.com/\n/a /b", externalRefused: "Not on Free.");
        Assert.Equal(new RedirectProblem(1, "Not on Free."), Assert.Single(refused.Problems));
        Assert.Null(refused.Match("/shop"));
        Assert.NotNull(refused.Match("/a"));
    }

    [Theory]
    [InlineData("/a /b 307", "The status must be 301")]
    [InlineData("/a /b 404", "The status must be 301")]
    [InlineData("/a /b 301!", "! (redirect even where a page exists) isn't supported")]
    [InlineData("/a /b !", "! (redirect even where a page exists) isn't supported")]
    [InlineData("/* /index.html 200", "200 (show another page at this address) isn't supported")]
    [InlineData("/a", "needs the old address and the new one")]
    [InlineData("/a /b 301 Country=us", "Conditions such as Country= aren't supported")]
    [InlineData("about /b", "The old address must start with /")]
    [InlineData("https://old.example/a /b", "must be a path on this site")]
    [InlineData("/?p=12 /b", "query strings can't be matched")]
    [InlineData("/a/../b /c", "can't have . or ..")]
    [InlineData("/blog*/x /b", "* only works as the last part")]
    [InlineData("/:1st /b", ":1st isn't a name")]
    [InlineData("/:id/:id /b", ":id appears twice")]
    [InlineData("/x/:splat /b", ":splat is for the new address")]
    [InlineData("/a /b/:splat", ":splat needs an old address ending in *")]
    [InlineData("/a /b/:slug", "uses :slug, which the old address doesn't have")]
    [InlineData("/a javascript:alert(1)", "must start with / (a page on this site) or https://")]
    [InlineData("/a mailto:x@example.com", "must start with / (a page on this site) or https://")]
    [InlineData("/a b.html", "must start with / (a page on this site) or https://")]
    [InlineData("/a //evil.example/", "starts with //")]
    [InlineData("/a /\\evil.example/", "a character that can't be in an address")]
    [InlineData("/a /b\"onload", "a character that can't be in an address")]
    [InlineData("/a /b<x>", "a character that can't be in an address")]
    [InlineData("/a https://", "isn't a valid web address")]
    [InlineData("/a /a", "sends the address to itself")]
    [InlineData("/About/ /about", "sends the address to itself")]
    public void Bad_lines_are_skipped_with_a_reason(string line, string reason) =>
        Assert.Contains(reason, Problem(line));

    [Fact]
    public void A_bad_line_doesnt_stop_the_others_and_problems_have_line_numbers()
    {
        var rules = RedirectRules.Parse("# header\n/a /b\n/c /d 307\n\n/e\n/f /g");
        Assert.Equal([3, 5], rules.Problems.Select(p => p.Line));
        Assert.Equal(["/a", "/f"], rules.Rules.Select(r => r.From));
        Assert.NotNull(rules.Match("/f"));
    }

    [Fact]
    public void Targets_longer_than_the_limit_are_refused()
    {
        Assert.Contains("longer than 2,000", Problem("/a /" + new string('x', RedirectRules.MaxTargetLength)));
        Assert.Empty(RedirectRules.Parse("/a /" + new string('x', RedirectRules.MaxTargetLength - 1)).Problems);
    }

    [Fact]
    public void Rules_after_the_first_thousand_are_skipped()
    {
        var text = string.Join('\n', Enumerable.Range(0, RedirectRules.MaxRules + 5).Select(i => $"/old{i} /new{i}"));
        var rules = RedirectRules.Parse(text);
        Assert.Equal(RedirectRules.MaxRules, rules.Count);
        var problem = Assert.Single(rules.Problems);
        Assert.Equal(RedirectRules.MaxRules + 1, problem.Line);
        Assert.NotNull(rules.Match($"/old{RedirectRules.MaxRules - 1}"));
        Assert.Null(rules.Match($"/old{RedirectRules.MaxRules}"));
    }

    [Fact]
    public void A_file_over_64_kb_stops_where_it_goes_over()
    {
        var line = "/" + new string('a', 100) + " /b";   // 105 bytes with its line break
        var text = string.Join('\n', Enumerable.Repeat(line, 700));
        var rules = RedirectRules.Parse(text);
        var problem = Assert.Single(rules.Problems);
        Assert.Contains("over 64 KB", problem.Message);
        Assert.Equal(RedirectRules.MaxBytes / (line.Length + 1) + 1, problem.Line);
    }

    [Fact]
    public void An_empty_file_has_no_rules_and_no_problems()
    {
        Assert.Equal(0, RedirectRules.Parse("").Count);
        Assert.Empty(RedirectRules.Parse("\n# nothing yet\n").Problems);
        Assert.Null(RedirectRules.Empty.Match("/anything"));
    }

    [Fact]
    public void Loops_are_found_unless_a_page_stops_them()
    {
        var rules = RedirectRules.Parse("/a /b\n/b /c\n/c /a\n/x /y");
        var loops = rules.FindLoops(_ => false);
        Assert.Equal([1, 2, 3], loops.Select(l => l.Line));
        Assert.Contains("/a → /b → /c → /a", loops[0].Message);

        Assert.Empty(rules.FindLoops(path => path == "/c"));
    }

    [Fact]
    public void A_redirect_to_another_website_or_through_a_pattern_isnt_a_loop()
    {
        Assert.Empty(RedirectRules.Parse("/a https://example.com/a\n/b /c").FindLoops(_ => false));
        Assert.Empty(RedirectRules.Parse("/a /blog/a\n/blog/* /posts/:splat").FindLoops(_ => false));
    }

    [Fact]
    public void A_chain_longer_than_the_limit_counts_as_a_loop()
    {
        var text = string.Join('\n', Enumerable.Range(0, RedirectRules.MaxHops + 3).Select(i => $"/p{i} /p{i + 1}"));
        var loops = RedirectRules.Parse(text).FindLoops(_ => false);
        Assert.Contains(loops, l => l.Line == 1);
    }

    [Fact]
    public void Rules_are_listed_as_written()
    {
        var rule = Assert.Single(RedirectRules.Parse("/Shop   https://shop.example/   302").Rules);
        Assert.Equal(new RedirectRule(1, "/Shop", "https://shop.example/", 302, External: true), rule);
    }
}
