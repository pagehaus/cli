using System.Net;
using System.Net.Sockets;
using Pagehaus.Cli.Serve;

namespace Pagehaus.Cli.Tests;

/// <summary>pagehaus serve, on a real port, against a small site.</summary>
public sealed class ServeTests : IAsyncLifetime
{
    private readonly string _root = Directory.CreateTempSubdirectory("pagehaus-serve").FullName;
    private LiveReload? _live;
    private WebApplication? _app;
    private HttpClient _client = null!;

    private void Write(string rel, string content)
    {
        var path = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public async Task InitializeAsync()
    {
        Write("includes/header.html", "<header><!--#echo var=\"title\" --></header>");
        Write("index.html", "<!--#set var=\"title\" value=\"Home\" --><!--#include virtual=\"/includes/header.html\" --><body><p>Hi</p></body>");
        Write("blog/index.shtml", "<p>Blog</p>");
        Write("style.css", "body{}");
        Write(".env", "SECRET=1");
        Write("404.html", "<!--#set var=\"title\" value=\"Lost\" --><!--#include virtual=\"/includes/header.html\" -->");
        Write("empty/readme.txt", "no index here");
        Write("counter.html", "<p><!--#counter start=\"1000\" --></p>");

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        _live = new LiveReload(_root);
        _app = ServeCommand.Build(_root, port, _live);
        await _app.StartAsync();
        _client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri($"http://localhost:{port}") };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        if (_app is not null) await _app.DisposeAsync();
        _live?.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Visitor_counters_show_a_sample_number()
    {
        var html = await _client.GetStringAsync("/counter.html");
        Assert.StartsWith("<p>2,234</p>", html);
    }

    [Fact]
    public async Task Pages_get_their_includes_and_the_reload_script()
    {
        var res = await _client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/html", res.Content.Headers.ContentType?.MediaType);
        var html = await res.Content.ReadAsStringAsync();
        Assert.StartsWith("<header>Home</header><body><p>Hi</p><script>", html);
        Assert.EndsWith("</script></body>", html);
    }

    [Fact]
    public async Task Folders_redirect_to_their_slash_and_show_their_index()
    {
        var res = await _client.GetAsync("/blog");
        Assert.Equal(HttpStatusCode.MovedPermanently, res.StatusCode);
        Assert.Equal("/blog/", res.Headers.Location?.OriginalString);
        Assert.Contains("<p>Blog</p>", await _client.GetStringAsync("/blog/"));
    }

    [Theory]
    [InlineData("/nope.html")]
    [InlineData("/.env")]
    [InlineData("/empty/")]
    [InlineData("/../etc/passwd")]
    public async Task Missing_and_hidden_files_get_the_sites_404_page(string path)
    {
        var res = await _client.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Contains("<header>Lost</header>", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Files_have_the_same_types_and_headers_as_on_pagehaus()
    {
        var res = await _client.GetAsync("/style.css");
        Assert.Equal("text/css; charset=utf-8", res.Content.Headers.ContentType?.ToString());
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await _client.PostAsync("/style.css", new StringContent(""))).StatusCode);
    }

    [Fact]
    public async Task Forms_show_what_pagehaus_would_send()
    {
        var res = await _client.PostAsync("/_form/contact", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["name"] = "Ada <3", ["_gotcha"] = "", ["_redirect"] = "/thanks.html",
        }));
        var html = await res.Content.ReadAsStringAsync();
        Assert.Contains("The “contact” form", html);
        Assert.Contains("<th>name</th><td>Ada &lt;3</td>", html);
        Assert.DoesNotContain("_gotcha", html);
        Assert.Contains("href=\"/thanks.html\"", html);
    }

    [Fact]
    public async Task Saving_a_file_tells_open_pages_to_reload()
    {
        using var events = await _client.GetAsync(LiveReload.EventsPath, HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await events.Content.ReadAsStreamAsync());
        Assert.Equal(": listening", await reader.ReadLineAsync());
        await reader.ReadLineAsync();

        Write("style.css", "body{color:red}");
        var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("data: reload", line);
    }
}
