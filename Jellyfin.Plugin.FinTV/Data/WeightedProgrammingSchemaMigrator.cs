using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FinTV.Data;

/// <summary>
/// Creates the additive schema used by weighted channels. Kept separate from the recovered FinTV
/// migrator so existing installations can adopt the feature without rewriting legacy migrations.
/// </summary>
internal static class WeightedProgrammingSchemaMigrator
{
    public static async Task MigrateAsync(FinTvDbContext db, ILogger logger, CancellationToken cancellationToken)
    {
        logger.LogInformation("Ensuring weighted programming schema");

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "ChannelProgrammingSettings" (
                "ChannelId" TEXT NOT NULL PRIMARY KEY,
                "Enabled" INTEGER NOT NULL DEFAULT 0,
                "FillerEnabled" INTEGER NOT NULL DEFAULT 1,
                "FillerChancePercent" INTEGER NOT NULL DEFAULT 100,
                "MinFillerItems" INTEGER NOT NULL DEFAULT 1,
                "MaxFillerItems" INTEGER NOT NULL DEFAULT 2,
                "MaxFillerSeconds" INTEGER NOT NULL DEFAULT 180,
                "FillerRepeatWindow" INTEGER NOT NULL DEFAULT 12,
                FOREIGN KEY("ChannelId") REFERENCES "Channels"("Id") ON DELETE CASCADE
            );
            """,
            cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "ChannelProgramSources" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "ChannelId" TEXT NOT NULL,
                "JellyfinItemId" TEXT NOT NULL,
                "TargetAirtimePercent" REAL NOT NULL DEFAULT 100,
                "PlaybackMode" INTEGER NOT NULL DEFAULT 0,
                "Enabled" INTEGER NOT NULL DEFAULT 1,
                "SortOrder" INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY("ChannelId") REFERENCES "Channels"("Id") ON DELETE CASCADE
            );
            """,
            cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE INDEX IF NOT EXISTS "IX_ChannelProgramSources_ChannelId_SortOrder"
            ON "ChannelProgramSources" ("ChannelId", "SortOrder");
            """,
            cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "ChannelFillerSources" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "ChannelId" TEXT NOT NULL,
                "JellyfinItemId" TEXT NOT NULL,
                "Kind" INTEGER NOT NULL DEFAULT 0,
                "Weight" INTEGER NOT NULL DEFAULT 1,
                "Enabled" INTEGER NOT NULL DEFAULT 1,
                "SortOrder" INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY("ChannelId") REFERENCES "Channels"("Id") ON DELETE CASCADE
            );
            """,
            cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE INDEX IF NOT EXISTS "IX_ChannelFillerSources_ChannelId_SortOrder"
            ON "ChannelFillerSources" ("ChannelId", "SortOrder");
            """,
            cancellationToken);

        await AddColumnIfMissingAsync(db, "PlayoutItems", "ProgramSourceId", "TEXT", cancellationToken);
        await AddColumnIfMissingAsync(db, "PlayoutHistory", "ProgramSourceId", "TEXT", cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS \"IX_PlayoutItems_ProgramSourceId\" ON \"PlayoutItems\" (\"ProgramSourceId\");",
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS \"IX_PlayoutHistory_ProgramSourceId\" ON \"PlayoutHistory\" (\"ProgramSourceId\");",
            cancellationToken);
    }

    private static async Task AddColumnIfMissingAsync(
        FinTvDbContext db,
        string table,
        string column,
        string definition,
        CancellationToken cancellationToken)
    {
        var escapedTable = table.Replace("\"", "\"\"");
        var escapedColumn = column.Replace("'", "''");
        var count = await db.Database
            .SqlQueryRaw<long>($"SELECT COUNT(*) AS \"Value\" FROM pragma_table_info(\"{escapedTable}\") WHERE name = '{escapedColumn}'")
            .FirstAsync(cancellationToken);

        if (count > 0)
        {
            return;
        }

        await db.Database.ExecuteSqlRawAsync(
            $"ALTER TABLE \"{escapedTable}\" ADD COLUMN \"{column}\" {definition};",
            cancellationToken);
    }
}
