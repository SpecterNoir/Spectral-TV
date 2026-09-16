using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SpectralTV.Services;

/// <summary>
/// Registers Spectral TV's native Jellyfin-Web Home integration with the optional JavaScript Injector
/// plugin. This is intentionally discovered through reflection so Spectral TV never takes a hard
/// runtime dependency on JavaScript Injector and cannot prevent Jellyfin from starting when it is absent.
/// </summary>
public sealed class JavaScriptInjectorRegistrar : BackgroundService
{
    private const string ScriptId = "spectral-tv-native-channels-home";
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private const int MaxAttempts = 24;

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

                if (assembly is not null && TryRegister(assembly))
                {
                    _logger.LogInformation(
                        "Spectral TV registered its native Channels Home integration with JavaScript Injector");
                    return;
                }
            }
            catch (Exception ex)
            {
                // Optional web integration must never affect Jellyfin startup or playback.
                _logger.LogDebug(
                    ex,
                    "Spectral TV could not register its optional JavaScript integration on attempt {Attempt}",
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

        _logger.LogInformation(
            "JavaScript Injector was not available; Spectral TV Channels will remain available through its other Jellyfin surfaces");
    }

    private bool TryRegister(Assembly injectorAssembly)
    {
        var interfaceType = injectorAssembly.GetType("Jellyfin.Plugin.JavaScriptInjector.PluginInterface");
        var register = interfaceType?.GetMethod("RegisterScript", BindingFlags.Public | BindingFlags.Static);
        if (register is null)
        {
            return false;
        }

        var parameter = register.GetParameters().SingleOrDefault();
        if (parameter is null)
        {
            return false;
        }

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

        var script = ReadScript();
        if (string.IsNullOrWhiteSpace(script))
        {
            _logger.LogWarning("Spectral TV native Channels Home script resource was empty");
            return false;
        }

        var plugin = Plugin.Instance;
        var payloadJson = JsonSerializer.Serialize(new
        {
            id = ScriptId,
            name = "Spectral TV - Channels Home Section",
            script,
            enabled = true,
            requiresAuthentication = true,
            pluginId = plugin?.Id.ToString("D") ?? "8a3f6c2d-5b4e-4d9a-a721-3e6f8c1b2d47",
            pluginName = plugin?.Name ?? "Spectral TV",
            pluginVersion = plugin?.Version.ToString() ?? "unknown"
        });

        var payload = parse.Invoke(null, [payloadJson]);
        if (payload is null)
        {
            return false;
        }

        var result = register.Invoke(null, [payload]);
        return result is true;
    }

    private static string ReadScript()
    {
        var assembly = typeof(JavaScriptInjectorRegistrar).Assembly;
        const string ResourceName = "Jellyfin.Plugin.SpectralTV.Configuration.nativeChannelsHome.js";
        using var stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            return string.Empty;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
