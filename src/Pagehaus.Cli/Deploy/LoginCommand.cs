using System.Text;

namespace Pagehaus.Cli.Deploy;

/// <summary>pagehaus login: checks an API token with Pagehaus and saves it; pagehaus logout forgets it.</summary>
public static class LoginCommand
{
    public static async Task<int> RunAsync()
    {
        Console.WriteLine($"Make an API token at {Config.Url}/account/api (\"Site files\" is enough for deploy), then paste it here.");
        Console.Write("Token: ");
        var token = ReadSecret().Trim();
        if (!(token.StartsWith("ph_", StringComparison.Ordinal) && token.Length == 46))
            throw new CliException("That doesn't look like a Pagehaus token: they start with ph_ and are 46 characters long.");

        using var api = PagehausApi.Create(token);
        var me = await api.MeAsync();
        new Config { Token = token }.Save();
        Console.WriteLine($"Logged in with \"{me.Token}\" ({Describe(me)}). Saved in {Config.FilePath}.");
        if (Environment.GetEnvironmentVariable(Config.TokenVariable) is { Length: > 0 })
            Console.WriteLine($"Note: {Config.TokenVariable} is set, and is used instead while it is.");
        return 0;
    }

    public static int Logout()
    {
        Config.Delete();
        Console.WriteLine("Logged out: the saved token is deleted. (It still works until you revoke it at " +
                          $"{Config.Url}/account/api.)");
        return 0;
    }

    public static string Describe(TokenInfo me) =>
        (me.Site is { } site ? $"site {site}, " : "all your sites, ") + (me.Access == "site-read" ? "read only" : "can deploy");

    /// <summary>A line typed without showing it; piped input (echo $TOKEN | pagehaus login) is read as it is.</summary>
    private static string ReadSecret()
    {
        if (Console.IsInputRedirected)
        {
            var piped = Console.ReadLine() ?? "";
            Console.WriteLine();
            return piped;
        }
        var text = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (text.Length > 0) text.Length--; }
            else if (!char.IsControl(key.KeyChar)) text.Append(key.KeyChar);
        }
        Console.WriteLine();
        return text.ToString();
    }
}
