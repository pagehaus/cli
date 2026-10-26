using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using Pagehaus.Cli.Deploy;

namespace Pagehaus.Cli.Tests;

/// <summary>pagehaus deploy, against a stand-in for the Pagehaus API.</summary>
[Collection("Environment")] // sets PAGEHAUS_URL and PAGEHAUS_TOKEN
public sealed class DeployTests : IAsyncLifetime
{
    private const string Token = "ph_abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQ";
    private readonly string _root = Directory.CreateTempSubdirectory("pagehaus-deploy").FullName;
    private WebApplication _api = null!;

    // What the stand-in API saw, and what it answers.
    private readonly List<string> _uploaded = [];
    private string? _uploadedTo;
    private bool _dry;
    private string[] _sites = ["bakery"];
    private string? _tokenSite;
    private int _status = 200;

    private void Write(string rel, string content)
    {
        var path = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public async Task InitializeAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port));
        builder.Logging.ClearProviders();
        _api = builder.Build();
        _api.Use(async (ctx, next) =>
        {
            if (ctx.Request.Headers.Authorization != $"Bearer {Token}")
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsJsonAsync(new { error = "This token is revoked." });
                return;
            }
            await next(ctx);
        });
        _api.MapGet("/api/v1/me", () => Results.Json(new { token = "laptop", access = _tokenSite is null ? "account" : "site-write", site = _tokenSite, plan = "Free" }));
        _api.MapGet("/api/v1/sites", () => Results.Json(_sites.Select(s => new { name = s, url = $"https://{s}.pagehaus.site/", suspended = false })));
        _api.MapPost("/api/v1/sites/{name}/zip", async (string name, HttpRequest req) =>
        {
            if (_status != 200) return Results.Json(new { error = "Your Free plan has room for 250 MB." }, statusCode: _status);
            _uploadedTo = name;
            _dry = req.Query["dry"] == "1";
            using var zip = new ZipArchive(req.Body.CanSeek ? req.Body : await Buffer(req.Body));
            _uploaded.AddRange(zip.Entries.Select(e => e.FullName));
            return Results.Json(new
            {
                extracted = _uploaded.Select(p => "/" + p), replaced = new[] { "/index.html" },
                removed = Array.Empty<object>(), stripped = (string?)null, dryRun = _dry,
            });
        });
        await _api.StartAsync();
        Environment.SetEnvironmentVariable("PAGEHAUS_URL", $"http://localhost:{port}");
        Environment.SetEnvironmentVariable("PAGEHAUS_TOKEN", Token);
    }

    private static async Task<Stream> Buffer(Stream body)
    {
        var ms = new MemoryStream();
        await body.CopyToAsync(ms);
        ms.Position = 0;
        return ms;
    }

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("PAGEHAUS_URL", null);
        Environment.SetEnvironmentVariable("PAGEHAUS_TOKEN", null);
        await _api.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }

    private static string Page(string body = "") => $"<!doctype html><html><head><title>T</title><meta name=\"description\" content=\"D\"></head><body>{body}</body></html>";

    [Fact]
    public async Task The_folder_is_uploaded_without_its_hidden_files_to_the_only_site()
    {
        Write("index.html", Page());
        Write("img/logo.svg", "<svg/>");
        Write(".git/HEAD", "ref");
        Write(".env", "SECRET=1");
        var output = new StringWriter();

        Assert.Equal(0, await DeployCommand.RunAsync(_root, site: null, dryRun: false, force: false, output));
        Assert.Equal("bakery", _uploadedTo);
        Assert.False(_dry);
        Assert.Equal(["img/logo.svg", "index.html"], _uploaded.Order());
        Assert.Contains("1 new: /img/logo.svg", output.ToString());
        Assert.Contains("1 replaced: /index.html", output.ToString());
        Assert.Contains("Done: https://bakery.pagehaus.site/", output.ToString());
    }

    [Fact]
    public async Task A_dry_run_changes_nothing_and_says_so()
    {
        Write("index.html", Page());
        var output = new StringWriter();
        await DeployCommand.RunAsync(_root, "bakery", dryRun: true, force: false, output);
        Assert.True(_dry);
        Assert.Contains("Dry run: nothing was changed", output.ToString());
        Assert.DoesNotContain("Done:", output.ToString());
    }

    [Fact]
    public async Task With_several_sites_it_asks_which_unless_the_token_is_for_one()
    {
        Write("index.html", Page());
        _sites = ["bakery", "band"];
        var e = await Assert.ThrowsAsync<CliException>(() => DeployCommand.RunAsync(_root, null, false, false, new StringWriter()));
        Assert.Contains("Add --site with one of: bakery, band", e.Message);

        _tokenSite = "band";
        await DeployCommand.RunAsync(_root, null, false, false, new StringWriter());
        Assert.Equal("band", _uploadedTo);
    }

    [Fact]
    public async Task Problems_stop_the_deploy_unless_forced()
    {
        Write("index.html", Page("""<a href="gone.html">x</a>"""));
        var e = await Assert.ThrowsAsync<CliException>(() => DeployCommand.RunAsync(_root, "bakery", false, false, new StringWriter()));
        Assert.Contains("found 1 problem", e.Message);
        Assert.Null(_uploadedTo);

        await DeployCommand.RunAsync(_root, "bakery", false, force: true, new StringWriter());
        Assert.Equal("bakery", _uploadedTo);
    }

    [Fact]
    public async Task Pagehaus_explains_what_went_wrong()
    {
        Write("index.html", Page());
        _status = 413;
        var e = await Assert.ThrowsAsync<CliException>(() => DeployCommand.RunAsync(_root, "bakery", false, false, new StringWriter()));
        Assert.Equal("Your Free plan has room for 250 MB.", e.Message);

        Environment.SetEnvironmentVariable("PAGEHAUS_TOKEN", "ph_revokedrevokedrevokedrevokedrevokedrevoked1");
        e = await Assert.ThrowsAsync<CliException>(() => DeployCommand.RunAsync(_root, "bakery", false, false, new StringWriter()));
        Assert.Contains("Your token wasn't accepted (This token is revoked.). Run pagehaus login", e.Message);
    }
}
