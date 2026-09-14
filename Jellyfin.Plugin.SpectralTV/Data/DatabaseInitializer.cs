using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SpectralTV.Data;

/// <summary>
/// Initializes the Spectral TV database without ever making Jellyfin startup depend on plugin state.
/// </summary>
public class DatabaseInitializer : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(IServiceScopeFactory scopeFactory, ILogger<DatabaseInitializer> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Initializes the plugin database. Any plugin-specific failure is contained so Jellyfin can continue starting.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SpectralTvDbContext>();
            await SchemaMigrator.MigrateAsync(db, _logger, cancellationToken);
            await WeightedProgrammingSchemaMigrator.MigrateAsync(db, _logger, cancellationToken);

            _logger.LogInformation("SpectralTV database initialized");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("SpectralTV database initialization was canceled during Jellyfin shutdown.");
        }
        catch (Exception ex)
        {
            // A plugin must never be able to prevent the Jellyfin host from starting.
            // Keep Spectral TV in a degraded state and surface the failure in the log instead.
            _logger.LogError(
                ex,
                "SpectralTV database initialization failed. Spectral TV will remain degraded, but Jellyfin startup will continue.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
