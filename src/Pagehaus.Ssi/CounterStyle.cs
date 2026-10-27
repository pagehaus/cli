using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Pagehaus.Ssi;

/// <summary>
/// How <c>&lt;!--#counter --&gt;</c> draws its number: plain text, a theme (digits in boxes) or the site's own digit
/// images. Attribute values become CSS, so none is ever passed through: each is parsed into a value from a fixed set,
/// and the CSS is written from that. Anything that doesn't parse makes the directive an error.
/// </summary>
public sealed partial class CounterStyle
{
    public const long MaxStart = 1_000_000_000;
    public const int MaxDigits = 12;

    public static readonly IReadOnlyList<string> Themes = ["classic", "odometer", "led", "none"];

    /// <summary>The 148 CSS colour names.</summary>
    public static readonly IReadOnlySet<string> ColorNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "aliceblue", "antiquewhite", "aqua", "aquamarine", "azure", "beige", "bisque", "black", "blanchedalmond", "blue",
        "blueviolet", "brown", "burlywood", "cadetblue", "chartreuse", "chocolate", "coral", "cornflowerblue", "cornsilk",
        "crimson", "cyan", "darkblue", "darkcyan", "darkgoldenrod", "darkgray", "darkgreen", "darkgrey", "darkkhaki",
        "darkmagenta", "darkolivegreen", "darkorange", "darkorchid", "darkred", "darksalmon", "darkseagreen",
        "darkslateblue", "darkslategray", "darkslategrey", "darkturquoise", "darkviolet", "deeppink", "deepskyblue",
        "dimgray", "dimgrey", "dodgerblue", "firebrick", "floralwhite", "forestgreen", "fuchsia", "gainsboro",
        "ghostwhite", "gold", "goldenrod", "gray", "green", "greenyellow", "grey", "honeydew", "hotpink", "indianred",
        "indigo", "ivory", "khaki", "lavender", "lavenderblush", "lawngreen", "lemonchiffon", "lightblue", "lightcoral",
        "lightcyan", "lightgoldenrodyellow", "lightgray", "lightgreen", "lightgrey", "lightpink", "lightsalmon",
        "lightseagreen", "lightskyblue", "lightslategray", "lightslategrey", "lightsteelblue", "lightyellow", "lime",
        "limegreen", "linen", "magenta", "maroon", "mediumaquamarine", "mediumblue", "mediumorchid", "mediumpurple",
        "mediumseagreen", "mediumslateblue", "mediumspringgreen", "mediumturquoise", "mediumvioletred", "midnightblue",
        "mintcream", "mistyrose", "moccasin", "navajowhite", "navy", "oldlace", "olive", "olivedrab", "orange",
        "orangered", "orchid", "palegoldenrod", "palegreen", "paleturquoise", "palevioletred", "papayawhip", "peachpuff",
        "peru", "pink", "plum", "powderblue", "purple", "rebeccapurple", "red", "rosybrown", "royalblue", "saddlebrown",
        "salmon", "sandybrown", "seagreen", "seashell", "sienna", "silver", "skyblue", "slateblue", "slategray",
        "slategrey", "snow", "springgreen", "steelblue", "tan", "teal", "thistle", "tomato", "turquoise", "violet", "wheat",
        "white", "whitesmoke", "yellow", "yellowgreen",
    };

    private static readonly Dictionary<string, string> Fonts = new(StringComparer.Ordinal)
    {
        ["mono"] = "ui-monospace,SFMono-Regular,Menlo,Consolas,monospace",
        ["serif"] = "Georgia,'Times New Roman',serif",
        ["sans"] = "system-ui,-apple-system,'Segoe UI',Roboto,sans-serif",
        ["inherit"] = "inherit",
    };

    private static readonly Dictionary<string, string> Sizes = new(StringComparer.Ordinal)
    {
        ["small"] = "0.8em", ["medium"] = "1em", ["large"] = "1.6em",
    };

    // Shown to screen readers only: the number once, instead of each box.
    private const string HiddenText =
        "position:absolute;width:1px;height:1px;overflow:hidden;clip-path:inset(50%);white-space:nowrap";

    [GeneratedRegex("^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$")]
    private static partial Regex HexColor();

    // Letters, digits and . _ - / only, with one * for the digit: nothing that could leave the src attribute.
    [GeneratedRegex(@"^[A-Za-z0-9._/-]*\*[A-Za-z0-9._/-]*$")]
    private static partial Regex ImagePattern();

    public long Start { get; private init; }
    public string Separator { get; private init; } = ",";
    public string? Theme { get; private init; }
    /// <summary>Site path of the digit images with {0} for the digit, e.g. "/counter/{0}.gif".</summary>
    public string? Images { get; private init; }
    public int Digits { get; private init; } = 1;
    public string? Color { get; private init; }
    public string? Background { get; private init; }
    public string? Border { get; private init; }
    public string? Size { get; private init; }
    public string? Font { get; private init; }
    public bool? Rounded { get; private init; }

    /// <summary>The style the attributes describe, or null if any of them is unknown, malformed or out of place.</summary>
    /// <param name="currentFile">The site-relative file the directive is in; relative image paths start from its folder.</param>
    public static CounterStyle? Parse(IReadOnlyList<(string Name, string Value)> attrs, string currentFile)
    {
        long start = 0;
        string? separator = null, theme = null, images = null, color = null, background = null, border = null, size = null, font = null;
        int? digits = null;
        bool? rounded = null;
        foreach (var (name, value) in attrs)
        {
            switch (name)
            {
                case "start" when long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var s) && s <= MaxStart:
                    start = s;
                    break;
                case "separator" when value.Length <= 1:
                    separator = value;
                    break;
                case "theme" when Themes.Contains(value):
                    theme = value;
                    break;
                case "images" when ParseImages(value, currentFile) is { } pattern:
                    images = pattern;
                    break;
                case "digits" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var d) && d is >= 1 and <= MaxDigits:
                    digits = d;
                    break;
                case "color" when ParseColor(value) is { } c:
                    color = c;
                    break;
                case "background" when ParseColor(value) is { } c:
                    background = c;
                    break;
                case "border" when value == "none" || ParseColor(value) is not null:
                    border = value == "none" ? "none" : ParseColor(value);
                    break;
                case "size" when ParseSize(value) is { } px:
                    size = px;
                    break;
                case "font" when Fonts.ContainsKey(value):
                    font = value;
                    break;
                case "corners" when value is "square" or "rounded":
                    rounded = value == "rounded";
                    break;
                // Unknown attributes are errors, so new ones can be added without changing existing pages.
                default:
                    return null;
            }
        }

        var looks = color ?? background ?? border ?? font ?? (rounded is null ? null : "corners");
        if (theme is null && images is null)
        {
            // Plain text: the theme settings mean nothing here, so they're mistakes.
            if (looks is not null || digits is not null || size is not null) return null;
        }
        else if (images is not null)
        {
            // The images are the look: only how many and how big.
            if (theme is not null || looks is not null) return null;
        }
        else if (theme == "none" && (looks is not null || size is not null))
        {
            return null; // no styles at all: the site's CSS does it
        }

        return new CounterStyle
        {
            Start = start,
            Separator = separator ?? (theme is null && images is null ? "," : ""),
            Theme = theme,
            Images = images,
            Digits = digits ?? (theme is null && images is null ? 1 : 6),
            Color = color, Background = background, Border = border, Size = size, Font = font, Rounded = rounded,
        };
    }

    /// <summary>"#abc", "#aabbcc" or a colour name (any case), as written in CSS; null for anything else.</summary>
    public static string? ParseColor(string value)
    {
        if (HexColor().IsMatch(value)) return value.ToLowerInvariant();
        var name = value.ToLowerInvariant();
        return ColorNames.Contains(name) ? name : null;
    }

    private static string? ParseSize(string value)
    {
        if (Sizes.TryGetValue(value, out var named)) return named;
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var px) && px is >= 10 and <= 96
            ? px + "px"
            : null;
    }

    /// <summary>"/counter/*.gif" (or relative to the page) as "/counter/{0}.gif"; null if it isn't a path inside the site.</summary>
    private static string? ParseImages(string value, string currentFile)
    {
        if (!ImagePattern().IsMatch(value) || value.StartsWith("//", StringComparison.Ordinal)) return null;
        var resolved = SitePath.Combine(SitePath.Parent(currentFile), value);
        if (resolved is null || resolved.Count(c => c == '*') != 1) return null;
        return "/" + resolved.Replace("*", "{0}");
    }

    /// <summary>The counter's HTML for <paramref name="visitors"/> (the site's total; <see cref="Start"/> is added here).</summary>
    public string Render(long visitors)
    {
        var n = visitors + Start;
        if (Theme is null && Images is null) return Group(n.ToString(CultureInfo.InvariantCulture), Separator);

        var digits = n.ToString(CultureInfo.InvariantCulture).PadLeft(Digits, '0');
        var kind = Images is null ? Theme! : "images";
        var sb = new StringBuilder();
        sb.Append("<span class=\"pagehaus-counter pagehaus-counter-").Append(kind).Append('"');
        if (Theme != "none") sb.Append(" style=\"").Append(WrapperCss()).Append('"');
        sb.Append('>');

        var digitCss = Theme is null or "none" ? null : DigitCss();
        for (var i = 0; i < digits.Length; i++)
        {
            if (i > 0 && Separator.Length > 0 && (digits.Length - i) % 3 == 0)
                sb.Append("<span class=\"pagehaus-counter-separator\" aria-hidden=\"true\">")
                    .Append(WebUtility.HtmlEncode(Separator)).Append("</span>");
            if (Images is not null)
            {
                sb.Append("<img class=\"pagehaus-counter-digit\" src=\"")
                    .Append(WebUtility.HtmlEncode(string.Format(CultureInfo.InvariantCulture, Images, digits[i]))).Append("\" alt=\"\"");
                if (Size is not null) sb.Append(" style=\"height:").Append(Size).Append('"');
                sb.Append('>');
            }
            else
            {
                sb.Append("<span class=\"pagehaus-counter-digit\" aria-hidden=\"true\"");
                if (digitCss is not null) sb.Append(" style=\"").Append(digitCss).Append('"');
                sb.Append('>').Append(digits[i]).Append("</span>");
            }
        }
        sb.Append("<span class=\"pagehaus-counter-text\" style=\"").Append(HiddenText).Append("\">")
            .Append(Group(n.ToString(CultureInfo.InvariantCulture), ",")).Append("</span></span>");
        return sb.ToString();
    }

    /// <summary>"12345" as "12,345", with any one-character separator (or none), HTML-encoded.</summary>
    private static string Group(string digits, string separator)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < digits.Length; i++)
        {
            if (i > 0 && (digits.Length - i) % 3 == 0) sb.Append(separator);
            sb.Append(digits[i]);
        }
        return WebUtility.HtmlEncode(sb.ToString());
    }

    private string WrapperCss()
    {
        if (Images is not null) return "display:inline-flex;align-items:flex-end;vertical-align:middle";
        var font = Font ?? (Theme == "odometer" ? "sans" : "mono");
        // Lining, equal-width figures: some fonts (Georgia) have old-style ones that sit at different heights.
        return $"display:inline-flex;align-items:center;gap:0.1em;vertical-align:middle;line-height:1;font-variant-numeric:lining-nums tabular-nums;" +
               $"font-family:{Fonts[font]};font-size:{Size ?? "1em"}";
    }

    private string DigitCss()
    {
        var (color, background, border, rounded) = Theme switch
        {
            "odometer" => ("#f4f4f4", "#222", "#000", true),
            "led" => ("#ff3b30", "#000", "#000", true),
            _ => ("#fff", "#000", "#555", false), // classic
        };
        color = Color ?? color;
        background = Background ?? background;
        border = Border ?? border;
        rounded = Rounded ?? rounded;

        var css = new StringBuilder("display:inline-block;min-width:0.7em;padding:0.15em 0.12em;text-align:center;font-weight:bold;");
        css.Append("color:").Append(color).Append(';');
        // The odometer's wheels: a shade over whatever colour they are.
        css.Append(Theme == "odometer"
            ? $"background:linear-gradient(rgba(0,0,0,.5),rgba(255,255,255,.15) 50%,rgba(0,0,0,.5)),{background};"
            : $"background:{background};");
        css.Append(border == "none" ? "border:0;" : $"border:1px solid {border};");
        css.Append("border-radius:").Append(rounded ? "0.2em" : "0").Append(';');
        if (Theme == "led") css.Append("text-shadow:0 0 0.3em ").Append(color).Append(';');
        return css.ToString().TrimEnd(';');
    }
}
