using System.Net;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;
using Pagehaus.Ssi;

namespace Pagehaus.Cli.Serve;

/// <summary>
/// Serves a folder the way Pagehaus serves a site: index pages (index.html, index.shtml, index.htm), Server Side
/// Includes in .html/.htm/.shtml, the site's 404.html, hidden files refused, the same content types. Forms can't be
/// sent from here, so they show what Pagehaus would email. Keep in step with the host's StaticSite.
/// </summary>
public sealed class SiteServer(string root, SsiProcessor ssi, LiveReload? reload = null)
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = CreateContentTypes();

    public string Root { get; } = Path.GetFullPath(root);

    public async Task HandleAsync(HttpContext ctx)
    {
        if (reload is not null && ctx.Request.Path == LiveReload.EventsPath)
        {
            await reload.StreamAsync(ctx);
            return;
        }
        if (ctx.Request.Path.StartsWithSegments("/_form"))
        {
            await FormAsync(ctx);
            return;
        }
        if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method))
        {
            ctx.Response.Headers.Allow = "GET, HEAD";
            ctx.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }
        await ServeAsync(ctx);
    }

    private async Task ServeAsync(HttpContext ctx)
    {
        var res = ctx.Response;
        res.Headers.XContentTypeOptions = "nosniff";
        res.Headers.CacheControl = "no-cache";

        var requestPath = ctx.Request.Path.Value ?? "/";
        var rel = requestPath.Contains('\\') ? null : SitePath.Normalize(requestPath);
        if (rel is null || IsHidden(rel))
        {
            await NotFoundAsync(ctx);
            return;
        }

        var disk = SitePath.ToDisk(Root, rel);
        if (Directory.Exists(disk))
        {
            if (!requestPath.EndsWith('/'))
            {
                var canonical = rel.Length == 0 ? "/" : "/" + string.Join('/', rel.Split('/').Select(Uri.EscapeDataString)) + "/";
                res.Redirect(canonical + ctx.Request.QueryString, permanent: true);
                return;
            }
            if (SitePath.FindIndex(Root, rel) is not { } index)
            {
                await NotFoundAsync(ctx);
                return;
            }
            rel = index;
            disk = SitePath.ToDisk(Root, rel);
        }
        else if (!File.Exists(disk))
        {
            await NotFoundAsync(ctx);
            return;
        }

        if (SsiProcessor.IsSsiFile(rel))
        {
            await PageAsync(ctx, rel, disk, StatusCodes.Status200OK);
            return;
        }
        var info = new FileInfo(disk);
        await Results.File(disk, ContentTypeFor(rel), lastModified: info.LastWriteTimeUtc,
            entityTag: new EntityTagHeaderValue($"\"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}\""),
            enableRangeProcessing: true).ExecuteAsync(ctx);
    }

    private async Task PageAsync(HttpContext ctx, string rel, string disk, int status)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        if (new FileInfo(disk).Length > SsiProcessor.MaxPageBytes)
        {
            // As on Pagehaus: a page this big is sent as it is, without processing includes.
            if (!HttpMethods.IsHead(ctx.Request.Method)) await ctx.Response.SendFileAsync(disk);
            return;
        }
        var html = await ssi.ProcessAsync(await File.ReadAllTextAsync(disk), new SsiContext
        {
            SiteRoot = Root,
            DocumentPath = rel,
            Host = ctx.Request.Host.Host,
            QueryString = ctx.Request.QueryString.Value ?? "",
            FollowLinks = true, // your own computer: deploy uploads what a link points to
        });
        if (reload is not null) html = LiveReload.AddScript(html);
        if (!HttpMethods.IsHead(ctx.Request.Method)) await ctx.Response.WriteAsync(html);
    }

    private async Task NotFoundAsync(HttpContext ctx)
    {
        var custom = SitePath.ToDisk(Root, "404.html");
        if (File.Exists(custom))
        {
            await PageAsync(ctx, "404.html", custom, StatusCodes.Status404NotFound);
            return;
        }
        await BuiltInPageAsync(ctx, StatusCodes.Status404NotFound, "Page not found",
            $"There's nothing at <code>{WebUtility.HtmlEncode(ctx.Request.Path.Value)}</code>. Add a <code>404.html</code> " +
            "to your site to customise this page.");
    }

    /// <summary>What Pagehaus would email for a form post (sending needs Pagehaus: /_form/{name}).</summary>
    private async Task FormAsync(HttpContext ctx)
    {
        if (!HttpMethods.IsPost(ctx.Request.Method) || !ctx.Request.HasFormContentType)
        {
            await BuiltInPageAsync(ctx, StatusCodes.Status405MethodNotAllowed, "Forms are sent with POST",
                "Use <code>method=\"post\"</code> on the form.");
            return;
        }
        var form = await ctx.Request.ReadFormAsync();
        var rows = string.Concat(form.Where(f => !f.Key.StartsWith('_'))
            .Select(f => $"<tr><th>{WebUtility.HtmlEncode(f.Key)}</th><td>{WebUtility.HtmlEncode(f.Value.ToString())}</td></tr>"));
        var redirect = form["_redirect"].ToString();
        var next = redirect.StartsWith('/') && !redirect.StartsWith("//")
            ? $"<p>Then visitors go to <a href=\"{WebUtility.HtmlEncode(redirect)}\">{WebUtility.HtmlEncode(redirect)}</a>.</p>"
            : "";
        await BuiltInPageAsync(ctx, StatusCodes.Status200OK, $"The “{ctx.Request.Path.Value!["/_form/".Length..]}” form",
            "On Pagehaus, this message would be emailed to you and kept on your site's Forms tab:" +
            $"<table>{rows}</table>{next}<p>Nothing was sent: this is a preview on your computer.</p>");
    }

    public static async Task BuiltInPageAsync(HttpContext ctx, int status, string title, string bodyHtml)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        if (HttpMethods.IsHead(ctx.Request.Method)) return;
        await ctx.Response.WriteAsync($$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <title>{{WebUtility.HtmlEncode(title)}}</title>
            <style>body{font:16px/1.5 system-ui,sans-serif;max-width:36rem;margin:15vh auto;padding:0 16px;color:#1a1a1a}
            h1{font-size:1.6rem;border-left:8px solid #e63b2e;padding-left:12px}footer{margin-top:3rem;color:#777;font-size:.85rem}
            th,td{text-align:left;padding:4px 12px 4px 0;vertical-align:top}</style>
            </head><body><h1>{{WebUtility.HtmlEncode(title)}}</h1><p>{{bodyHtml}}</p>
            <footer>pagehaus serve · preview on your computer</footer></body></html>
            """);
    }

    /// <summary>Dotfiles and dot-folders (.git, .env, ...) are never served, except .well-known.</summary>
    public static bool IsHidden(string rel) => rel.Split('/').Any(s => s.StartsWith('.') && s != ".well-known");

    public static string ContentTypeFor(string rel)
    {
        if (!ContentTypes.TryGetContentType(rel, out var type)) return "application/octet-stream";
        return type.StartsWith("text/") || type is "application/javascript" or "application/json" or "image/svg+xml"
            ? type + "; charset=utf-8"
            : type;
    }

    private static FileExtensionContentTypeProvider CreateContentTypes()
    {
        var p = new FileExtensionContentTypeProvider();
        p.Mappings[".shtml"] = "text/html";
        p.Mappings[".webmanifest"] = "application/manifest+json";
        p.Mappings[".avif"] = "image/avif";
        return p;
    }
}
