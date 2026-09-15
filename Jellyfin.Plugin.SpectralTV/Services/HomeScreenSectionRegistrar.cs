using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SpectralTV.Services;

/// <summary>
/// Registers Spectral TV's on-demand channel row with the optional Home Screen Sections plugin.
/// The integration is discovered at runtime through reflection so Spectral TV never takes a hard
/// dependency on Home Screen Sections and Jellyfin remains fully usable when that plugin is absent.
/// </summary>
public sealed class HomeScreenSectionRegistrar : BackgroundService
{
    /// <summary>
    /// Stable section id used by Home Screen Sections user settings.
    /// </summary>
    public static readonly Guid ChannelsSectionId = Guid.Parse("7e765efe-bfe9-430f-b921-24de8ae4b8ae");

    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private const int MaxAttempts = 24;

    private readonly ILogger<HomeScreenSectionRegistrar> _logger;

    public HomeScreenSectionRegistrar(ILogger<HomeScreenSectionRegistrar> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        for (var attempt = 1; attempt <= MaxAttempts && !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                var assembly = AssemblyLoadContext.All
                    .SelectMany(context => context.Assemblies)
                    .FirstOrDefault(candidate =>
                        candidate.FullName?.Contains(".HomeScreenSections", StringComparison.OrdinalIgnoreCase) == true);

                if (assembly is not null && TryRegister(assembly))
                {
                    _logger.LogInformation(
                        "Spectral TV registered the Channels home section with Home Screen Sections");
                    return;
                }
            }
            catch (Exception ex)
            {
                // Optional UI integration must never affect Jellyfin startup or core Spectral playback.
                _logger.LogDebug(ex, "Spectral TV could not register the optional Channels home section on attempt {Attempt}", attempt);
            }

            try
            {
                await Task.Delay(RetryDelay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }

        _logger.LogInformation(
            "Home Screen Sections was not available; Spectral TV on-demand channels will continue to use their normal Jellyfin playlist fallback");
    }

    private bool TryRegister(Assembly homeScreenAssembly)
    {
        var interfaceType = homeScreenAssembly.GetType("Jellyfin.Plugin.HomeScreenSections.PluginInterface");
        var register = interfaceType?.GetMethod("RegisterSection", BindingFlags.Public | BindingFlags.Static);
        if (register is null)
        {
            return false;
        }

        var parameter = register.GetParameters().SingleOrDefault();
        if (parameter is null)
        {
            return false;
        }

        // RegisterSection currently accepts Newtonsoft.Json.Linq.JObject. Construct it through the
        // reflected parameter type so Spectral TV does not need to ship its own Newtonsoft assembly.
        var parse = parameter.ParameterType.GetMethod(
            "Parse",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: [typeof(string)],
            modifiers: null);
        if (parse is null)
        {
            return false;
        }

        var payloadJson = JsonSerializer.Serialize(new
        {
            id = ChannelsSectionId,
            displayText = "Channels",
            limit = 1,
            additionalData = "spectral-tv-on-demand",
            resultsAssembly = typeof(ChannelsHomeSectionResults).Assembly.FullName,
            resultsClass = typeof(ChannelsHomeSectionResults).FullName,
            resultsMethod = nameof(ChannelsHomeSectionResults.GetResults)
        });

        var payload = parse.Invoke(null, [payloadJson]);
        if (payload is null)
        {
            return false;
        }

        register.Invoke(null, [payload]);
        return true;
    }
}
