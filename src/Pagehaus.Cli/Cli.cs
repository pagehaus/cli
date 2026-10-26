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
