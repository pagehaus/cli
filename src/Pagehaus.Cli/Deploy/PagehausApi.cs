using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pagehaus.Cli.Deploy;

/// <summary>The Pagehaus API (https://pagehaus.app/help/api) as far as the tool needs it.</summary>
public sealed class PagehausApi(HttpClient http) : IDisposable
{
    public static PagehausApi Create(string token, string? baseUrl = null)
    {
        var http = new HttpClient { BaseAddress = new Uri((baseUrl ?? Config.Url) + "/api/v1/"), Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("pagehaus-cli", Cli.Version));
        return new PagehausApi(http);
    }

    public Task<TokenInfo> MeAsync() => SendAsync(new HttpRequestMessage(HttpMethod.Get, "me"), ApiJson.Default.TokenInfo);

    public Task<List<SiteInfo>> SitesAsync() => SendAsync(new HttpRequestMessage(HttpMethod.Get, "sites"), ApiJson.Default.ListSiteInfo);

    public Task<ZipResult> UploadZipAsync(string site, Stream zip, bool dryRun) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Post, $"sites/{Uri.EscapeDataString(site)}/zip{(dryRun ? "?dry=1" : "")}")
        {
            Content = new StreamContent(zip) { Headers = { ContentType = new MediaTypeHeaderValue("application/zip") } },
        }, ApiJson.Default.ZipResult);

    private async Task<T> SendAsync<T>(HttpRequestMessage request, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        HttpResponseMessage res;
        try
        {
            res = await http.SendAsync(request);
        }
        catch (HttpRequestException e)
        {
            throw new CliException($"Couldn't reach {http.BaseAddress?.GetLeftPart(UriPartial.Authority)}: {e.Message}");
        }
        using (res)
        {
            if (res.IsSuccessStatusCode)
                return await res.Content.ReadFromJsonAsync(type) ?? throw new CliException("Pagehaus sent an empty answer.");
            var message = await ErrorAsync(res);
            throw new CliException(res.StatusCode switch
            {
                HttpStatusCode.Unauthorized => $"Your token wasn't accepted ({message}). Run pagehaus login with a new one.",
                _ => message,
            });
        }
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage res)
    {
        try
        {
            if (await res.Content.ReadFromJsonAsync(ApiJson.Default.ApiError) is { Error.Length: > 0 } e) return e.Error;
        }
        catch (JsonException)
        {
            // not the API's error format (a proxy's page, say)
        }
        return $"Pagehaus answered {(int)res.StatusCode} {res.ReasonPhrase}.";
    }

    public void Dispose() => http.Dispose();
}

public sealed record TokenInfo(string Token, string Access, string? Site, DateTime? Expires, string Plan);
public sealed record SiteInfo(string Name, string Url, bool Suspended);
public sealed record ZipResult(List<string> Extracted, List<string> Replaced, List<RemovedFile> Removed, string? Stripped, bool DryRun);
public sealed record RemovedFile(string Path, string Reason);
public sealed record ApiError(string Error);

[JsonSerializable(typeof(TokenInfo))]
[JsonSerializable(typeof(List<SiteInfo>))]
[JsonSerializable(typeof(ZipResult))]
[JsonSerializable(typeof(ApiError))]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
internal sealed partial class ApiJson : JsonSerializerContext;
