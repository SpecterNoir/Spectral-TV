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
        if (applied > 0) return;

        var channelsTable = await db.Database
            .SqlQueryRaw<long>("SELECT COUNT(*) AS \"Value\" FROM sqlite_master WHERE type = 'table' AND name = 'Channels'")
            .FirstAsync(cancellationToken);
        if (channelsTable > 0)
        {
            await MigrateChannelNumberAsync(db, logger, cancellationToken);
        }

        await db.Database.ExecuteSqlRawAsync(
            "INSERT OR IGNORE INTO \"__SpectralTvSchema\" (\"Key\", \"AppliedAt\") VALUES ({0}, {1})",
            new object[] { ChannelNumberMigrationKey, DateTime.UtcNow.ToString("O") },
            cancellationToken);
    }

    private static async Task MigrateChannelNumberAsync(SpectralTvDbContext db, ILogger logger, CancellationToken cancellationToken)
    {
        var type = await db.Database
            .SqlQueryRaw<string>("SELECT type AS \"Value\" FROM pragma_table_info('Channels') WHERE name = 'Number'")
            .FirstOrDefaultAsync(cancellationToken);
        if (string.Equals(type, "REAL", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "DOUBLE", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "FLOAT", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        logger.LogInformation("Migrating Spectral TV channel numbers to decimal values");
        await db.Database.ExecuteSqlRawAsync("DROP INDEX IF EXISTS \"IX_Channels_Number\";", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Channels\" ADD COLUMN \"NumberReal\" REAL NOT NULL DEFAULT 1;", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("UPDATE \"Channels\" SET \"NumberReal\" = CAST(\"Number\" AS REAL);", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Channels\" DROP COLUMN \"Number\";", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Channels\" RENAME COLUMN \"NumberReal\" TO \"Number\";", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Channels_Number\" ON \"Channels\" (\"Number\");", cancellationToken);
    }
}
