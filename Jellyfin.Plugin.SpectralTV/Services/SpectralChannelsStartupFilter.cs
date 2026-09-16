using System.Globalization;
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

    private readonly ILogger<SpectralChannelsStartupFilter> _logger;
    private int _loggedSuccess;

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
                var bodyClose = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                if (bodyClose >= 0)
                {
                    // Keep executable code out of index.html. A normal external script request is
                    // observable, cache-controllable, and follows the same proven loading model as
                    // JavaScript Injector. The relative URL also preserves Jellyfin base paths.
                    var cacheKey = typeof(SpectralChannelsStartupFilter).Assembly
                        .GetName()
                        .Version?
                        .ToString()
                        ?? DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
                    var block = $"{StartMarker}\n<script defer src=\"../SpectralTV/web/channels-home.js?v={cacheKey}\"></script>\n{EndMarker}\n";
                    html = html[..bodyClose] + block + html[bodyClose..];

                    if (Interlocked.Exchange(ref _loggedSuccess, 1) == 0)
                    {
                        _logger.LogInformation("Spectral TV injected the external Channels Home bridge loader into Jellyfin Web");
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

}
