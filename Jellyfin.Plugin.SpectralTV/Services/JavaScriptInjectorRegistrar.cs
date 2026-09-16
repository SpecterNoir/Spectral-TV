using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SpectralTV.Services;

/// <summary>
/// Removes Spectral TV's legacy JavaScript Injector registration after upgrades.
/// Spectral TV now injects its Channels browser bridge directly through its own
/// request-time startup filter and no longer depends on JavaScript Injector.
/// </summary>
public sealed class JavaScriptInjectorRegistrar : BackgroundService
{
    private const string ScriptId = "spectral-tv-native-channels-home";
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private const int MaxAttempts = 12;

    private readonly ILogger<JavaScriptInjectorRegistrar> _logger;

    public JavaScriptInjectorRegistrar(ILogger<JavaScriptInjectorRegistrar> logger)
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
                        candidate.FullName?.Contains(".JavaScriptInjector", StringComparison.OrdinalIgnoreCase) == true);

                if (assembly is not null)
                {
                    TryUnregisterLegacyScript(assembly);
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "Spectral TV could not clean up its legacy JavaScript Injector entry on attempt {Attempt}",
                    attempt);
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
    }

    private void TryUnregisterLegacyScript(Assembly injectorAssembly)
    {
        var interfaceType = injectorAssembly.GetType("Jellyfin.Plugin.JavaScriptInjector.PluginInterface");
        var unregister = interfaceType?.GetMethod(
            "UnregisterScript",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: [typeof(string)],
            modifiers: null);

        if (unregister is null)
        {
            return;
        }

        var result = unregister.Invoke(null, [ScriptId]);
        if (result is true)
        {
            _logger.LogInformation(
                "Spectral TV removed its obsolete JavaScript Injector Channels bridge registration");
        }
    }
}
