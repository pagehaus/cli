using System.Reflection;

namespace Pagehaus.Cli;

/// <summary>The command line: <c>pagehaus &lt;command&gt; [folder] [options]</c>.</summary>
public static class Cli
{
    public static string Version =>
        typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "dev";

    private const string Usage = """
        pagehaus: preview, check and deploy plain HTML sites

        Usage:
          pagehaus serve [folder] [--port 8080] [--open] [--no-reload]
              Preview the site at http://localhost:8080 as Pagehaus serves it: Server Side Includes,
              index pages, your 404.html. Pages reload in the browser when you save a file.

          pagehaus check [folder] [--strict]
              Find broken links and missing files, links whose case doesn't match the file
              (Team.JPG linked as team.jpg), files too big, pages without a title or description.
              Exit status 1 if there are problems (with --strict, warnings too): ready for CI.

          pagehaus login
              Save an API token from pagehaus.app/account/api, for deploy. (Or set PAGEHAUS_TOKEN.)

          pagehaus deploy [folder] [--site name] [--dry-run] [--force]
              Upload the folder to your Pagehaus site: new and changed files are written; files you
              deleted here stay on the site. Runs check first and stops on problems unless --force.
              --dry-run shows what would change. --site can be left out if you have one site.

          pagehaus logout
          pagehaus --version
          pagehaus help

        The folder is the current one if you leave it out. More: https://github.com/pagehaus/cli
        """;

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            switch (args)
            {
                case [] or ["help" or "--help" or "-h", ..]:
                    Console.WriteLine(Usage);
                    return args.Length == 0 ? 1 : 0;
                case ["--version" or "-v" or "version"]:
                    Console.WriteLine(Version);
                    return 0;
                case ["serve", .. var rest]:
                    var options = new Options(rest, flags: ["--open", "--no-reload"], values: ["--port"]);
                    return await Serve.ServeCommand.RunAsync(options.Folder, options.Int("--port") ?? 8080,
                        options.Has("--open"), reload: !options.Has("--no-reload"));
                case ["check", .. var rest]:
                    var check = new Options(rest, flags: ["--strict"], values: []);
                    return await Check.CheckCommand.RunAsync(check.Folder, check.Has("--strict"), Console.Out);
                case ["login"]:
                    return await Deploy.LoginCommand.RunAsync();
                case ["logout"]:
                    return Deploy.LoginCommand.Logout();
                case ["deploy", .. var rest]:
                    var deploy = new Options(rest, flags: ["--dry-run", "--force"], values: ["--site"]);
                    return await Deploy.DeployCommand.RunAsync(deploy.Folder, deploy.Value("--site"), deploy.Has("--dry-run"),
                        deploy.Has("--force"), Console.Out);
                default:
                    Console.Error.WriteLine($"Unknown command \"{args[0]}\". Run pagehaus help to see the commands.");
                    return 2;
            }
        }
        catch (CliException e)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }
    }
}

/// <summary>A mistake in how the tool was called, or something it can't do: shown as a message, without a stack trace.</summary>
public sealed class CliException(string message) : Exception(message);

/// <summary>A command's arguments: one optional folder, flags, and options with a value ("--port 8080" or "--port=8080").</summary>
public sealed class Options
{
    private readonly HashSet<string> _flags = [];
    private readonly Dictionary<string, string> _values = [];

    public string Folder { get; } = ".";

    public Options(IReadOnlyList<string> args, IReadOnlyCollection<string> flags, IReadOnlyCollection<string> values)
    {
        string? folder = null;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            var eq = arg.IndexOf('=');
            var name = arg.StartsWith("--") && eq > 0 ? arg[..eq] : arg;
            if (flags.Contains(name)) _flags.Add(name);
            else if (values.Contains(name))
            {
                var value = eq > 0 ? arg[(eq + 1)..] : i + 1 < args.Count ? args[++i] : throw new CliException($"{name} needs a value.");
                _values[name] = value;
            }
            else if (arg.StartsWith('-')) throw new CliException($"Unknown option {arg}. Run pagehaus help to see the options.");
            else if (folder is null) folder = arg;
            else throw new CliException($"Only one folder, please: \"{folder}\" or \"{arg}\".");
        }
        Folder = folder ?? ".";
    }

    public bool Has(string flag) => _flags.Contains(flag);

    public string? Value(string name) => _values.GetValueOrDefault(name);

    public int? Int(string name) =>
        Value(name) is not { } v ? null : int.TryParse(v, out var n) ? n : throw new CliException($"{name} needs a number, not \"{v}\".");
}
