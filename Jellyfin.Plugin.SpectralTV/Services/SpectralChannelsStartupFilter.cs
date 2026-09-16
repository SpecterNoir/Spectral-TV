using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SpectralTV.Services;

/// <summary>
/// Injects Spectral TV's Channels home bridge directly into Jellyfin Web at request time.
/// This uses the same ASP.NET Core startup-filter pattern proven by JavaScript Injector, but
/// keeps Spectral TV independent from any optional cross-plugin registration API.
/// </summary>
public sealed class SpectralChannelsStartupFilter : IStartupFilter
{
    internal const string StartMarker = "<!-- BEGIN Spectral TV Channels Home -->";
    internal const string EndMarker = "<!-- END Spectral TV Channels Home -->";

    private static readonly Lazy<string> BridgeScript = new(ReadBridgeScript);
    private readonly ILogger<SpectralChannelsStartupFilter> _logger;
    private int _loggedSuccess;
    private int _loggedMissingScript;

    public SpectralChannelsStartupFilter(ILogger<SpectralChannelsStartupFilter> logger)
    {
        _logger = logger;
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use(InvokeAsync);
            next(app);
        };
    }

    private async Task InvokeAsync(HttpContext context, Func<Task> nextMiddleware)
    {
        if (!HttpMethods.IsGet(context.Request.Method) || !IsIndexRequest(context.Request.Path.Value))
        {
            await nextMiddleware().ConfigureAwait(false);
            return;
        }

        // Force a complete uncompressed web shell so the response can be safely rewritten.
        context.Request.Headers.Remove("Accept-Encoding");
        context.Request.Headers.Remove("Range");
        context.Request.Headers.Remove("If-Range");

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await nextMiddleware().ConfigureAwait(false);
        }
        catch
        {
            context.Response.Body = originalBody;
            throw;
        }

        context.Response.Body = originalBody;
        buffer.Seek(0, SeekOrigin.Begin);

        var isHtml = context.Response.StatusCode == StatusCodes.Status200OK
            && (context.Response.ContentType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) ?? false);
        if (!isHtml)
        {
            await buffer.CopyToAsync(originalBody).ConfigureAwait(false);
            return;
        }

        string html;
        using (var reader = new StreamReader(buffer, Encoding.UTF8, true, 1024, leaveOpen: true))
        {
            html = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        try
        {
            if (!html.Contains(StartMarker, StringComparison.OrdinalIgnoreCase))
            {
                var script = BridgeScript.Value;
                if (string.IsNullOrWhiteSpace(script))
                {
                    if (Interlocked.Exchange(ref _loggedMissingScript, 1) == 0)
                    {
                        _logger.LogWarning("Spectral TV Channels browser bridge resource was empty; Home integration was not injected");
                    }
                }
                else
                {
                    var bodyClose = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                    if (bodyClose >= 0)
                    {
                        var block = $"{StartMarker}\n<script>\n{script}\n</script>\n{EndMarker}\n";
                        html = html[..bodyClose] + block + html[bodyClose..];

                        if (Interlocked.Exchange(ref _loggedSuccess, 1) == 0)
                        {
                            _logger.LogInformation("Spectral TV injected the Channels Home bridge directly into Jellyfin Web");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Home integration must never make Jellyfin Web unavailable.
            _logger.LogWarning(ex, "Spectral TV Channels web injection failed; serving the original Jellyfin page");
        }

        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html;charset=utf-8";
        context.Response.ContentLength = bytes.Length;
        context.Response.Headers.Remove("ETag");
        context.Response.Headers.Remove("Last-Modified");
        context.Response.Headers.Remove("Accept-Ranges");
        await originalBody.WriteAsync(bytes).ConfigureAwait(false);
    }

    private static bool IsIndexRequest(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        return path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/web", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadBridgeScript()
    {
        var assembly = typeof(SpectralChannelsStartupFilter).Assembly;
        const string resourceName = "Jellyfin.Plugin.SpectralTV.Configuration.nativeChannelsHome.js";
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return string.Empty;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
