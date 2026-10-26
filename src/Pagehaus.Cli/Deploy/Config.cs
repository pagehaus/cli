using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pagehaus.Cli.Deploy;

/// <summary>
/// Where the tool finds Pagehaus and your API token: PAGEHAUS_TOKEN (for CI) or the token saved by pagehaus login,
/// in ~/.config/pagehaus/config.json (%APPDATA%\pagehaus\config.json on Windows), readable only by you.
/// PAGEHAUS_URL points the tool at another Pagehaus (development).
/// </summary>
public sealed class Config
{
    public const string TokenVariable = "PAGEHAUS_TOKEN";
    public const string DefaultUrl = "https://pagehaus.app";

    public string? Token { get; set; }

    public static string Url => (Environment.GetEnvironmentVariable("PAGEHAUS_URL") is { Length: > 0 } url ? url : DefaultUrl).TrimEnd('/');

    public static string FilePath
    {
        get
        {
            var dir = OperatingSystem.IsWindows()
                ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
                : Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg
                    ? xdg
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(dir, "pagehaus", "config.json");
        }
    }

    public static Config Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize(File.ReadAllText(FilePath), ConfigJson.Default.Config) ?? new Config()
                : new Config();
        }
        catch (JsonException)
        {
            throw new CliException($"{FilePath} isn't valid. Delete it and run pagehaus login again.");
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        // Private from the moment it's created, so the token is never readable by others, even briefly.
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            if (File.Exists(FilePath)) File.SetUnixFileMode(FilePath, options.UnixCreateMode.Value); // an older, looser file
        }
        using var file = new FileStream(FilePath, options);
        JsonSerializer.Serialize(file, this, ConfigJson.Default.Config);
    }

    public static void Delete()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }

    /// <summary>The token to use: PAGEHAUS_TOKEN, else the saved one.</summary>
    public static string? CurrentToken() =>
        Environment.GetEnvironmentVariable(TokenVariable) is { Length: > 0 } fromEnv ? fromEnv.Trim() : Load().Token;
}

[JsonSerializable(typeof(Config))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class ConfigJson : JsonSerializerContext;
