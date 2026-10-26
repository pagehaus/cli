using System.Threading.Channels;

namespace Pagehaus.Cli.Serve;

/// <summary>
/// Reloads open pages when a file in the site changes: pages get a small script that listens to
/// <see cref="EventsPath"/> (server-sent events), and a change in the folder tells every listener to reload.
/// </summary>
public sealed class LiveReload : IDisposable
{
    public const string EventsPath = "/_pagehaus/events";
    private const string Script = $"""<script>new EventSource("{EventsPath}").onmessage = () => location.reload();</script>""";
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(150); // editors save in several steps

    private readonly FileSystemWatcher _watcher;
    private readonly Lock _gate = new();
    private readonly List<Channel<bool>> _listeners = [];
    private readonly Timer _timer;

    public LiveReload(string root)
    {
        _timer = new Timer(_ => Broadcast());
        _watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true, EnableRaisingEvents = true };
        _watcher.Changed += OnChange;
        _watcher.Created += OnChange;
        _watcher.Deleted += OnChange;
        _watcher.Renamed += OnChange;
    }

    /// <summary>The page with the reload script before its closing &lt;/body&gt; (or at the end).</summary>
    public static string AddScript(string html)
    {
        var body = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return body < 0 ? html + Script : html.Insert(body, Script);
    }

    /// <summary>Keeps the request open and sends "reload" each time the site changes.</summary>
    public async Task StreamAsync(HttpContext ctx)
    {
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        var channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        lock (_gate) _listeners.Add(channel);
        try
        {
            await ctx.Response.WriteAsync(": listening\n\n", ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            await foreach (var _ in channel.Reader.ReadAllAsync(ctx.RequestAborted))
            {
                await ctx.Response.WriteAsync("data: reload\n\n", ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            }
        }
        catch (OperationCanceledException)
        {
            // The page was closed or reloaded.
        }
        finally
        {
            lock (_gate) _listeners.Remove(channel);
        }
    }

    private void OnChange(object sender, FileSystemEventArgs e)
    {
        // Hidden files (.git, editors' swap files) aren't part of the site.
        var rel = Path.GetRelativePath(_watcher.Path, e.FullPath).Replace('\\', '/');
        if (SiteServer.IsHidden(rel) || rel.EndsWith('~')) return;
        _timer.Change(Settle, Timeout.InfiniteTimeSpan);
    }

    private void Broadcast()
    {
        lock (_gate)
            foreach (var listener in _listeners) listener.Writer.TryWrite(true);
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _timer.Dispose();
    }
}
