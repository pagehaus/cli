using System.Text.RegularExpressions;

namespace Pagehaus.Ssi.Tests;

/// <summary>&lt;!--#counter --&gt; themes, their settings and digit images: what they draw, and what they refuse.</summary>
public sealed partial class CounterStyleTests
{
    private const string Error = "[an error occurred while processing this directive]";

    private static Task<string> Run(string attributes, long total = 42, string doc = "index.html") =>
        new SsiProcessor().ProcessAsync($"<!--#counter {attributes} -->", new SsiContext
        {
            SiteRoot = Path.GetTempPath(), DocumentPath = doc, VisitorCount = () => ValueTask.FromResult(total),
        });

    [GeneratedRegex("""class="pagehaus-counter-digit"[^>]*>""")]
    private static partial Regex Digit();

    [GeneratedRegex("""<span class="pagehaus-counter-digit"[^>]*>(\d)</span>""")]
    private static partial Regex DigitText();

    private static string Shown(string html) => string.Concat(DigitText().Matches(html).Select(m => m.Groups[1].Value));

    [Fact]
    public async Task Classic_draws_padded_digits_in_boxes_and_tells_screen_readers_the_number_once()
    {
        const string box = "display:inline-block;min-width:0.7em;padding:0.15em 0.12em;text-align:center;font-weight:bold;" +
                           "color:#fff;background:#000;border:1px solid #555;border-radius:0";
        Assert.Equal(
            """<span class="pagehaus-counter pagehaus-counter-classic" style="display:inline-flex;align-items:center;gap:0.1em;vertical-align:middle;line-height:1;font-variant-numeric:lining-nums tabular-nums;font-family:ui-monospace,SFMono-Regular,Menlo,Consolas,monospace;font-size:1em">""" +
            $"""<span class="pagehaus-counter-digit" aria-hidden="true" style="{box}">0</span>""" +
            $"""<span class="pagehaus-counter-digit" aria-hidden="true" style="{box}">4</span>""" +
            $"""<span class="pagehaus-counter-digit" aria-hidden="true" style="{box}">2</span>""" +
            """<span class="pagehaus-counter-text" style="position:absolute;width:1px;height:1px;overflow:hidden;clip-path:inset(50%);white-space:nowrap">42</span></span>""",
            await Run("""theme="classic" digits="3" """));
    }

    [Fact]
    public async Task Digits_pad_to_six_by_default_and_never_cut_a_longer_number()
    {
        Assert.Equal("000042", Shown(await Run("""theme="classic" """)));
        Assert.Equal("1234567", Shown(await Run("""theme="classic" digits="2" """, 1234567)));
        Assert.Contains(">1,234,567</span>", await Run("""theme="classic" """, 1234567));       // what screen readers hear
        Assert.Equal("001042", Shown(await Run("""theme="led" start="1000" """)));
    }

    [Fact]
    public async Task Themes_have_no_separator_unless_asked()
    {
        Assert.DoesNotContain("separator", await Run("""theme="odometer" """, 1234567));
        var html = await Run("""theme="odometer" separator="." """, 1234567);
        Assert.Equal(2, Regex.Count(html, """<span class="pagehaus-counter-separator" aria-hidden="true">\.</span>"""));
    }

    [Fact]
    public async Task Each_theme_has_its_own_look_and_settings_replace_one_part_of_it()
    {
        var odometer = await Run("""theme="odometer" """);
        Assert.Contains("background:linear-gradient(rgba(0,0,0,.5),rgba(255,255,255,.15) 50%,rgba(0,0,0,.5)),#222", odometer);
        Assert.Contains("font-family:system-ui", odometer);
        Assert.Contains("border-radius:0.2em", odometer);

        var led = await Run("""theme="led" color="Lime" """);
        Assert.Contains("color:lime;", led);
        Assert.Contains("text-shadow:0 0 0.3em lime", led);

        var custom = await Run("""theme="odometer" color="#F4E7C5" background="saddlebrown" border="none" size="large" font="serif" corners="square" """);
        Assert.Contains("color:#f4e7c5;", custom);
        Assert.Contains("50%,rgba(0,0,0,.5)),saddlebrown;", custom);
        Assert.Contains("border:0;", custom);
        Assert.Contains("border-radius:0", custom);
        Assert.Contains("font-size:1.6em", custom);
        Assert.Contains("font-family:Georgia,'Times New Roman',serif", custom);
        Assert.Contains("font-size:24px", await Run("""theme="classic" size="24" """));
        Assert.Contains("font-family:inherit", await Run("""theme="classic" font="inherit" """));
    }

    [Fact]
    public async Task Theme_none_has_class_names_and_no_styles()
    {
        var html = await Run("""theme="none" digits="2" """);
        Assert.Equal(
            """<span class="pagehaus-counter pagehaus-counter-none">""" +
            """<span class="pagehaus-counter-digit" aria-hidden="true">4</span><span class="pagehaus-counter-digit" aria-hidden="true">2</span>""" +
            """<span class="pagehaus-counter-text" style="position:absolute;width:1px;height:1px;overflow:hidden;clip-path:inset(50%);white-space:nowrap">42</span></span>""",
            html);
    }

    [Fact]
    public async Task Digit_images_come_from_the_site()
    {
        var html = await Run("""images="/counter/*.gif" digits="3" """);
        Assert.Contains("""<img class="pagehaus-counter-digit" src="/counter/0.gif" alt="">""", html);
        Assert.Contains("""<img class="pagehaus-counter-digit" src="/counter/4.gif" alt="">""", html);
        Assert.Contains("""<img class="pagehaus-counter-digit" src="/counter/2.gif" alt="">""", html);
        Assert.Equal(3, Digit().Count(html));

        // Relative to the page, like a link; and sized by height.
        var relative = await Run("""images="digits/*.png" size="32" """, 7, "blog/post.html");
        Assert.Contains("""src="/blog/digits/7.png" alt="" style="height:32px">""", relative);
        Assert.Contains("""src="/blog/digits/0.png" """.TrimEnd(), relative);
    }

    [Theory]
    // Not a theme setting without a theme; not a look with images or theme="none".
    [InlineData("""digits="6" """)]
    [InlineData("""color="red" """)]
    [InlineData("""size="large" """)]
    [InlineData("""corners="rounded" """)]
    [InlineData("""theme="none" color="red" """)]
    [InlineData("""theme="none" size="20" """)]
    [InlineData("""images="/c/*.gif" color="red" """)]
    [InlineData("""images="/c/*.gif" theme="classic" """)]
    // Out of range or not a value we know.
    [InlineData("""theme="Classic" """)]
    [InlineData("""theme="classic" digits="0" """)]
    [InlineData("""theme="classic" digits="13" """)]
    [InlineData("""theme="classic" size="9" """)]
    [InlineData("""theme="classic" size="97" """)]
    [InlineData("""theme="classic" size="10px" """)]
    [InlineData("""theme="classic" size="1e2" """)]
    [InlineData("""theme="classic" font="Comic Sans MS" """)]
    [InlineData("""theme="classic" corners="round" """)]
    [InlineData("""theme="classic" color="#12345" """)]
    [InlineData("""theme="classic" color="rgb(0,0,0)" """)]
    [InlineData("""theme="classic" color="transparent" """)]
    // Attempts to add CSS or leave the attribute.
    [InlineData("""theme="classic" color="red;background:url(https://evil.example/x)" """)]
    [InlineData("""theme="classic" color="red\"onmouseover=\"alert(1)" """)]
    [InlineData("""theme="classic" background="url(x)" """)]
    [InlineData("""theme="classic" border="1px solid red" """)]
    [InlineData("""theme="classic" font="mono;position:fixed" """)]
    [InlineData("""theme="classic" size="20;position:fixed" """)]
    // Images from outside the site, or not a pattern.
    [InlineData("""images="https://evil.example/*.gif" """)]
    [InlineData("""images="//evil.example/*.gif" """)]
    [InlineData("""images="javascript:*" """)]
    [InlineData("""images="/../*.gif" """)]
    [InlineData("""images="/c/x\" onerror=\"alert(1)*.gif" """)]
    [InlineData("""images="/c/0.gif" """)]
    [InlineData("""images="/c/**.gif" """)]
    [InlineData("""images="/c/{0}*.gif" """)]
    public async Task Anything_that_does_not_parse_is_an_error(string attributes) =>
        Assert.Equal(Error, await Run(attributes));

    [Fact]
    public void There_are_148_colour_names() => Assert.Equal(148, CounterStyle.ColorNames.Count);
}
