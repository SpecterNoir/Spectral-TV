using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SpectralTV.Data;

/// <summary>Applies only the compatibility migration still needed by the current channel model.</summary>
internal static class SchemaMigrator
{
    private const string ChannelNumberMigrationKey = "channel-number-decimal";

    public static async Task MigrateAsync(SpectralTvDbContext db, ILogger logger, CancellationToken cancellationToken)
    {
        await db.Database.EnsureCreatedAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "__SpectralTvSchema" (
                "Key" TEXT NOT NULL PRIMARY KEY,
                "AppliedAt" TEXT NOT NULL
            );
            """,
            cancellationToken);

        var applied = await db.Database
            .SqlQueryRaw<long>("SELECT COUNT(*) AS \"Value\" FROM \"__SpectralTvSchema\" WHERE \"Key\" = {0}", ChannelNumberMigrationKey)
            .FirstAsync(cancellationToken);
        if (applied > 0)
        {
            return;
        }

        var channelsTable = await SqliteSchemaHelper.TableExistsAsync(db, "Channels", cancellationToken);
        if (channelsTable)
        {
            var type = await db.Database
                .SqlQueryRaw<string>("SELECT type AS \"Value\" FROM pragma_table_info('Channels') WHERE name = 'Number'")
                .FirstOrDefaultAsync(cancellationToken);

            // Older Spectral/FinTV databases used INTEGER affinity for Number. SQLite does not enforce
            // that affinity and safely stores values such as 34.5 as REAL when they cannot be represented
            // as integers. Rebuilding a user's Channels table merely to change the declared affinity is
            // therefore unnecessary and, on a partially completed old migration, can leave startup stuck
            // forever on a duplicate NumberReal column. Preserve the user's table and let EF read/write the
            // numeric value normally. New databases are still created with REAL affinity by the model.
            if (!string.IsNullOrWhiteSpace(type)
                && !string.Equals(type, "REAL", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(type, "DOUBLE", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(type, "FLOAT", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation(
                    "Leaving legacy Channels.Number SQLite affinity {Affinity} in place; SQLite numeric storage supports decimal channel numbers without a destructive table rewrite.",
                    type);
            }

            if (await SqliteSchemaHelper.ColumnExistsAsync(db, "Channels", "NumberReal", cancellationToken))
            {
                logger.LogWarning(
                    "Detected an unused Channels.NumberReal column from an interrupted legacy migration. It is being left in place safely; Spectral TV continues to use Channels.Number.");
            }
        }

        await db.Database.ExecuteSqlRawAsync(
            "INSERT OR IGNORE INTO \"__SpectralTvSchema\" (\"Key\", \"AppliedAt\") VALUES ({0}, {1})",
            new object[] { ChannelNumberMigrationKey, DateTime.UtcNow.ToString("O") },
            cancellationToken);
    }
}
