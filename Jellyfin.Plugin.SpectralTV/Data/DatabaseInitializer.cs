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
    /// Initializes the plugin database. Each additive schema family is isolated so one damaged legacy
    /// migration can never prevent newer automatic-channel or on-demand tables from being repaired.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SpectralTvDbContext>();

            var failures = 0;
            failures += await RunMigrationAsync(
                "core compatibility",
                () => SchemaMigrator.MigrateAsync(db, _logger, cancellationToken),
                cancellationToken);
            failures += await RunMigrationAsync(
                "automatic programming",
                () => WeightedProgrammingSchemaMigrator.MigrateAsync(db, _logger, cancellationToken),
                cancellationToken);
            failures += await RunMigrationAsync(
                "on-demand channels",
                () => OnDemandSchemaMigrator.MigrateAsync(db, _logger, cancellationToken),
                cancellationToken);

            if (failures == 0)
            {
                _logger.LogInformation("SpectralTV database initialized and verified");
            }
            else
            {
                _logger.LogWarning(
                    "SpectralTV database initialization completed with {FailureCount} isolated schema failure(s). Jellyfin will continue running; unaffected Spectral TV features remain available.",
                    failures);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("SpectralTV database initialization was canceled during Jellyfin shutdown.");
        }
        catch (Exception ex)
        {
            // Scope/DI failures are also plugin-local. A plugin must never prevent the host from starting.
            _logger.LogError(
                ex,
                "SpectralTV database initialization could not start. Spectral TV will remain degraded, but Jellyfin startup will continue.");
        }
    }

    private async Task<int> RunMigrationAsync(
        string name,
        Func<Task> migration,
        CancellationToken cancellationToken)
    {
        try
        {
            await migration().ConfigureAwait(false);
            _logger.LogInformation("SpectralTV {MigrationName} schema is ready", name);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "SpectralTV {MigrationName} schema migration failed. Continuing with the remaining independent schema repairs.",
                name);
            return 1;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
