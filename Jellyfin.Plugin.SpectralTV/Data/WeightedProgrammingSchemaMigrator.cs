using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SpectralTV.Data;

/// <summary>
/// Creates the additive schema used by automatic live programming. Kept separate from the recovered
/// SpectralTV migrator so existing installations can adopt the feature without rewriting legacy migrations.
/// </summary>
internal static class WeightedProgrammingSchemaMigrator
{
    public static async Task MigrateAsync(SpectralTvDbContext db, ILogger logger, CancellationToken cancellationToken)
    {
        logger.LogInformation("Ensuring automatic programming schema");

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "ChannelProgrammingSettings" (
                "ChannelId" TEXT NOT NULL PRIMARY KEY,
                "Enabled" INTEGER NOT NULL DEFAULT 0,
                "SelectionMode" INTEGER NOT NULL DEFAULT 1,
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

        await SqliteSchemaHelper.AddColumnIfMissingAsync(
            db,
            "ChannelProgrammingSettings",
            "SelectionMode",
            "INTEGER NOT NULL DEFAULT 1",
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

        // Playout tables belong to the recovered base schema. Very old/partially recovered databases can
        // be missing one of them. Repair the additive column/index only when the owning table exists so a
        // legacy base-table problem cannot block the automatic-programming or on-demand schema families.
        if (await SqliteSchemaHelper.TableExistsAsync(db, "PlayoutItems", cancellationToken))
        {
            await SqliteSchemaHelper.AddColumnIfMissingAsync(
                db,
                "PlayoutItems",
                "ProgramSourceId",
                "TEXT",
                cancellationToken);
            await db.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IF NOT EXISTS \"IX_PlayoutItems_ProgramSourceId\" ON \"PlayoutItems\" (\"ProgramSourceId\");",
                cancellationToken);
        }
        else
        {
            logger.LogWarning("Legacy PlayoutItems table is missing; skipping its optional automatic-programming index repair.");
        }

        if (await SqliteSchemaHelper.TableExistsAsync(db, "PlayoutHistory", cancellationToken))
        {
            await SqliteSchemaHelper.AddColumnIfMissingAsync(
                db,
                "PlayoutHistory",
                "ProgramSourceId",
                "TEXT",
                cancellationToken);
            await db.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IF NOT EXISTS \"IX_PlayoutHistory_ProgramSourceId\" ON \"PlayoutHistory\" (\"ProgramSourceId\");",
                cancellationToken);
        }
        else
        {
            logger.LogWarning("Legacy PlayoutHistory table is missing; skipping its optional automatic-programming index repair.");
        }
    }
}
