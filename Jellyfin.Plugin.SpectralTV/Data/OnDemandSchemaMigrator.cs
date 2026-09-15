using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SpectralTV.Data;

/// <summary>
/// Additive migration for on-demand channel recipes and per-user progress. Kept separate from the
/// recovered legacy schema so upgrades remain non-destructive.
/// </summary>
internal static class OnDemandSchemaMigrator
{
    public static async Task MigrateAsync(SpectralTvDbContext db, ILogger logger, CancellationToken cancellationToken)
    {
        logger.LogInformation("Ensuring on-demand channel schema");

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "OnDemandChannels" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "Name" TEXT NOT NULL,
                "Enabled" INTEGER NOT NULL DEFAULT 1,
                "ProgrammingMode" INTEGER NOT NULL DEFAULT 0,
                "RotationMode" INTEGER NOT NULL DEFAULT 0,
                "CustomPatternJson" TEXT NOT NULL DEFAULT '[]',
                "FillerEnabled" INTEGER NOT NULL DEFAULT 1,
                "FillerBeforeFirstProgram" INTEGER NOT NULL DEFAULT 1,
                "FillerBetweenPrograms" INTEGER NOT NULL DEFAULT 1,
                "FillerOnSourceChangeOnly" INTEGER NOT NULL DEFAULT 0,
                "MinFillerItems" INTEGER NOT NULL DEFAULT 1,
                "MaxFillerItems" INTEGER NOT NULL DEFAULT 1,
                "FillerRepeatWindow" INTEGER NOT NULL DEFAULT 10,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            """,
            cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS \"IX_OnDemandChannels_Name\" ON \"OnDemandChannels\" (\"Name\");",
            cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "OnDemandSources" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "ChannelId" TEXT NOT NULL,
                "JellyfinItemId" TEXT NOT NULL,
                "Weight" INTEGER NOT NULL DEFAULT 1,
                "BlockSize" INTEGER NOT NULL DEFAULT 1,
                "PlaybackMode" INTEGER NOT NULL DEFAULT 0,
                "Enabled" INTEGER NOT NULL DEFAULT 1,
                "SortOrder" INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY("ChannelId") REFERENCES "OnDemandChannels"("Id") ON DELETE CASCADE
            );
            """,
            cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS \"IX_OnDemandSources_ChannelId_SortOrder\" ON \"OnDemandSources\" (\"ChannelId\", \"SortOrder\");",
            cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "OnDemandFillerSources" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "ChannelId" TEXT NOT NULL,
                "JellyfinItemId" TEXT NOT NULL,
                "Kind" INTEGER NOT NULL DEFAULT 0,
                "Weight" INTEGER NOT NULL DEFAULT 1,
                "Enabled" INTEGER NOT NULL DEFAULT 1,
                "SortOrder" INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY("ChannelId") REFERENCES "OnDemandChannels"("Id") ON DELETE CASCADE
            );
            """,
            cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS \"IX_OnDemandFillerSources_ChannelId_SortOrder\" ON \"OnDemandFillerSources\" (\"ChannelId\", \"SortOrder\");",
            cancellationToken);

        // ExecuteSqlRawAsync formats raw SQL through composite formatting. Literal JSON object braces
        // therefore must be doubled here so SQLite receives '{}' instead of EF treating the braces as
        // format placeholders. The previous unescaped defaults threw FormatException during startup,
        // preventing this table (and therefore every on-demand write) from being created on real installs.
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "OnDemandProgress" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "ChannelId" TEXT NOT NULL,
                "ProgressKey" TEXT NOT NULL,
                "PatternIndex" INTEGER NOT NULL DEFAULT 0,
                "LastSourceId" TEXT NULL,
                "CurrentSourceId" TEXT NULL,
                "SourceCursorJson" TEXT NOT NULL DEFAULT '{{}}',
                "SourcePickCountJson" TEXT NOT NULL DEFAULT '{{}}',
                "ShuffleBagJson" TEXT NOT NULL DEFAULT '[]',
                "RecentFillerJson" TEXT NOT NULL DEFAULT '[]',
                "CurrentItemId" TEXT NULL,
                "CurrentPositionTicks" INTEGER NOT NULL DEFAULT 0,
                "UpdatedAt" TEXT NOT NULL,
                FOREIGN KEY("ChannelId") REFERENCES "OnDemandChannels"("Id") ON DELETE CASCADE
            );
            """,
            cancellationToken);

        await SqliteSchemaHelper.AddColumnIfMissingAsync(
            db,
            "OnDemandProgress",
            "CurrentSourceId",
            "TEXT NULL",
            cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_OnDemandProgress_ChannelId_ProgressKey\" ON \"OnDemandProgress\" (\"ChannelId\", \"ProgressKey\");",
            cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "OnDemandPlaylistLinks" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "ChannelId" TEXT NOT NULL,
                "UserId" TEXT NOT NULL,
                "JellyfinPlaylistId" TEXT NOT NULL,
                "QueueProgramCount" INTEGER NOT NULL DEFAULT 24,
                "LastSyncedAt" TEXT NOT NULL,
                FOREIGN KEY("ChannelId") REFERENCES "OnDemandChannels"("Id") ON DELETE CASCADE
            );
            """,
            cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_OnDemandPlaylistLinks_ChannelId_UserId\" ON \"OnDemandPlaylistLinks\" (\"ChannelId\", \"UserId\");",
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_OnDemandPlaylistLinks_JellyfinPlaylistId\" ON \"OnDemandPlaylistLinks\" (\"JellyfinPlaylistId\");",
            cancellationToken);

        // Existing weighted-programming installs need this column. Keeping the operation idempotent protects upgrades.
        await SqliteSchemaHelper.AddColumnIfMissingAsync(
            db,
            "ChannelProgrammingSettings",
            "SelectionMode",
            "INTEGER NOT NULL DEFAULT 1",
            cancellationToken);
    }
}
