using Pagehaus.Ssi;

namespace Pagehaus.Cli.Check;

/// <summary>pagehaus check: prints what's wrong; exit status 1 if there are problems (warnings alone don't fail).</summary>
public static class CheckCommand
{
    private const int FilesShown = 3;

    public static async Task<int> RunAsync(string folder, bool strict, TextWriter output)
    {
        if (!Directory.Exists(folder)) throw new CliException($"There's no folder \"{folder}\".");
        var checker = new SiteChecker(new SsiProcessor());
        var findings = await checker.CheckAsync(folder);
        output.WriteLine($"Checked {Path.GetFullPath(folder)}: {Count(checker.Pages, "page")}, {Count(checker.Files, "file")}.");
        output.WriteLine();

        foreach (var f in findings)
        {
            var label = f.Severity == Severity.Problem ? "problem" : "warning";
            Colour(output, f.Severity == Severity.Problem ? ConsoleColor.Red : ConsoleColor.Yellow, label);
            if (f.Files.Count == 1)
                output.WriteLine($": {f.Files[0]}: {f.Message}");
            else
            {
                var more = f.Files.Count > FilesShown ? $" and {f.Files.Count - FilesShown} more" : "";
                output.WriteLine($": {f.Message}");
                output.WriteLine($"         in {string.Join(", ", f.Files.Take(FilesShown))}{more}");
            }
        }

        var problems = findings.Count(f => f.Severity == Severity.Problem);
        var warnings = findings.Count - problems;
        if (findings.Count > 0) output.WriteLine();
        output.WriteLine(problems + warnings == 0
            ? "No problems found."
            : $"{Count(problems, "problem")}, {Count(warnings, "warning")}.");
        return problems > 0 || (strict && warnings > 0) ? 1 : 0;
    }

    private static string Count(int n, string what) => $"{n} {what}{(n == 1 ? "" : "s")}";

    private static void Colour(TextWriter output, ConsoleColor colour, string text)
    {
        var console = output == Console.Out && !Console.IsOutputRedirected;
        if (console) Console.ForegroundColor = colour;
        output.Write(text);
        if (console) Console.ResetColor();
    }
}
