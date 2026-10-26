using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Pagehaus.Ssi;

namespace Pagehaus.Cli.Serve;

/// <summary>pagehaus serve: the site on http://localhost:{port}, until Ctrl+C.</summary>
public static class ServeCommand
{
    public static async Task<int> RunAsync(string folder, int port, bool open, bool reload)
    {
        if (!Directory.Exists(folder)) throw new CliException($"There's no folder \"{folder}\".");
        port = FreePort(port);
        using var live = reload ? new LiveReload(Path.GetFullPath(folder)) : null;
        await using var app = Build(folder, port, live);
        await app.StartAsync();

        var url = $"http://localhost:{port}/";
        Console.WriteLine($"Serving {Path.GetFullPath(folder)}");
        Console.WriteLine($"  {url}{(reload ? "  (pages reload when you save)" : "")}");
        Console.WriteLine("Press Ctrl+C to stop.");
        if (open) OpenBrowser(url);
        await app.WaitForShutdownAsync();
        return 0;
    }

    public static WebApplication Build(string folder, int port, LiveReload? live)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders(); // the tool prints its own lines
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port));
        var app = builder.Build();
        var site = new SiteServer(folder, new SsiProcessor(), live);
        app.Run(site.HandleAsync);
        return app;
    }

    /// <summary><paramref name="wanted"/>, or the next free port if it's taken (another preview, say).</summary>
    private static int FreePort(int wanted)
    {
        for (var port = wanted; port < wanted + 20; port++)
        {
            try
            {
                using var probe = new TcpListener(IPAddress.Loopback, port);
                probe.Start();
                return port;
            }
            catch (SocketException)
            {
                // taken: try the next one
            }
        }
        throw new CliException($"Ports {wanted} to {wanted + 19} are all in use. Choose another with --port.");
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.WriteLine($"Couldn't open a browser: open {url} yourself.");
        }
    }
}
