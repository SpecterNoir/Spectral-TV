using Jellyfin.Plugin.SpectralTV.Data;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SpectralTV.Api;

/// <summary>
/// Admin-only database diagnostics and idempotent repair for Spectral TV.
/// This exists because upgraded installations can have a very different SQLite history
/// from the clean database used by normal startup smoke tests.
/// </summary>
[ApiController]
[Route("SpectralTV/api/diagnostics")]
[Authorize(Policy = Policies.RequiresElevation)]
public class DiagnosticsController : ControllerBase
{
    private readonly SpectralTvDbContext _db;
    private readonly ILogger<DiagnosticsController> _logger;

    public DiagnosticsController(SpectralTvDbContext db, ILogger<DiagnosticsController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Returns the current Spectral TV database health without changing user data.
    /// </summary>
    [HttpGet("status")]
    public async Task<ActionResult<object>> GetStatus(CancellationToken cancellationToken)
    {
        return Ok(await InspectAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Re-runs all additive Spectral TV schema families independently, then reports the exact result.
    /// The migrations are intentionally idempotent and do not delete channels or playback progress.
    /// </summary>
    [HttpPost("repair")]
    public async Task<ActionResult<object>> Repair(CancellationToken cancellationToken)
    {
        var repairs = new List<RepairStep>();

        await RunRepairAsync(
            "core compatibility",
            () => SchemaMigrator.MigrateAsync(_db, _logger, cancellationToken),
            repairs,
            cancellationToken).ConfigureAwait(false);
        await RunRepairAsync(
            "automatic programming",
            () => WeightedProgrammingSchemaMigrator.MigrateAsync(_db, _logger, cancellationToken),
            repairs,
            cancellationToken).ConfigureAwait(false);
        await RunRepairAsync(
            "on-demand channels",
            () => OnDemandSchemaMigrator.MigrateAsync(_db, _logger, cancellationToken),
            repairs,
            cancellationToken).ConfigureAwait(false);

        // Clear any EF tracking left over from an interrupted request before checking the repaired schema.
        _db.ChangeTracker.Clear();
        var status = await InspectAsync(cancellationToken).ConfigureAwait(false);

        return Ok(new
        {
            healthy = status.Healthy,
            status,
            repairs
        });
    }

    private async Task<DatabaseHealth> InspectAsync(CancellationToken cancellationToken)
    {
        var issues = new List<string>();
        string? connectionError = null;
        string? writeError = null;
        var canConnect = false;
        var canWrite = false;

        try
        {
            canConnect = await _db.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false);
            if (!canConnect)
            {
                issues.Add("Spectral TV cannot connect to its SQLite database.");
            }
        }
        catch (Exception ex)
        {
            connectionError = Describe(ex);
            issues.Add($"Database connection failed: {connectionError}");
        }

        if (canConnect)
        {
            await RequireTableAsync("Channels", issues, cancellationToken).ConfigureAwait(false);
            await RequireColumnAsync("Channels", "Number", issues, cancellationToken).ConfigureAwait(false);
            await RequireTableAsync("ChannelProgrammingSettings", issues, cancellationToken).ConfigureAwait(false);
            await RequireColumnAsync("ChannelProgrammingSettings", "SelectionMode", issues, cancellationToken).ConfigureAwait(false);
            await RequireTableAsync("ChannelProgramSources", issues, cancellationToken).ConfigureAwait(false);
            await RequireTableAsync("ChannelFillerSources", issues, cancellationToken).ConfigureAwait(false);
            await RequireTableAsync("OnDemandChannels", issues, cancellationToken).ConfigureAwait(false);
            await RequireTableAsync("OnDemandSources", issues, cancellationToken).ConfigureAwait(false);
            await RequireTableAsync("OnDemandFillerSources", issues, cancellationToken).ConfigureAwait(false);
            await RequireTableAsync("OnDemandProgress", issues, cancellationToken).ConfigureAwait(false);
            await RequireColumnAsync("OnDemandProgress", "CurrentSourceId", issues, cancellationToken).ConfigureAwait(false);
            await RequireTableAsync("OnDemandPlaylistLinks", issues, cancellationToken).ConfigureAwait(false);

            // Verify that the main database is actually writable. All DDL is inside a transaction
            // that is rolled back, so this leaves no diagnostic table behind.
            try
            {
                await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await _db.Database.ExecuteSqlRawAsync(
                    "CREATE TABLE IF NOT EXISTS \"__SpectralWriteProbe\" (\"Id\" INTEGER NOT NULL);",
                    cancellationToken).ConfigureAwait(false);
                await _db.Database.ExecuteSqlRawAsync(
                    "DROP TABLE IF EXISTS \"__SpectralWriteProbe\";",
                    cancellationToken).ConfigureAwait(false);
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                canWrite = true;
            }
            catch (Exception ex)
            {
                writeError = Describe(ex);
                issues.Add($"Database is not writable: {writeError}");
            }
        }

        return new DatabaseHealth
        {
            Healthy = canConnect && canWrite && issues.Count == 0,
            CanConnect = canConnect,
            CanWrite = canWrite,
            DatabasePath = Plugin.Instance?.DatabasePath ?? "unknown",
            ConnectionError = connectionError,
            WriteError = writeError,
            Issues = issues
        };
    }

    private async Task RequireTableAsync(string table, List<string> issues, CancellationToken cancellationToken)
    {
        try
        {
            if (!await SqliteSchemaHelper.TableExistsAsync(_db, table, cancellationToken).ConfigureAwait(false))
            {
                issues.Add($"Missing table: {table}");
            }
        }
        catch (Exception ex)
        {
            issues.Add($"Could not inspect table {table}: {Describe(ex)}");
        }
    }

    private async Task RequireColumnAsync(string table, string column, List<string> issues, CancellationToken cancellationToken)
    {
        try
        {
            if (!await SqliteSchemaHelper.ColumnExistsAsync(_db, table, column, cancellationToken).ConfigureAwait(false))
            {
                issues.Add($"Missing column: {table}.{column}");
            }
        }
        catch (Exception ex)
        {
            issues.Add($"Could not inspect column {table}.{column}: {Describe(ex)}");
        }
    }

    private async Task RunRepairAsync(
        string name,
        Func<Task> repair,
        List<RepairStep> repairs,
        CancellationToken cancellationToken)
    {
        try
        {
            await repair().ConfigureAwait(false);
            repairs.Add(new RepairStep { Name = name, Success = true });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var detail = Describe(ex);
            repairs.Add(new RepairStep { Name = name, Success = false, Error = detail });
            _logger.LogError(ex, "Spectral TV manual {RepairName} database repair failed", name);
        }
    }

    private static string Describe(Exception ex)
        => $"{ex.GetType().Name}: {ex.Message}";

    public sealed class RepairStep
    {
        public string Name { get; set; } = string.Empty;

        public bool Success { get; set; }

        public string? Error { get; set; }
    }

    public sealed class DatabaseHealth
    {
        public bool Healthy { get; set; }

        public bool CanConnect { get; set; }

        public bool CanWrite { get; set; }

        public string DatabasePath { get; set; } = string.Empty;

        public string? ConnectionError { get; set; }

        public string? WriteError { get; set; }

        public List<string> Issues { get; set; } = new();
    }
}
