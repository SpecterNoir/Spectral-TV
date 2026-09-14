using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SpectralTV.Data;

internal static class SchemaMigrator
{
    private const string ChannelNumberMigrationKey = "channel-number-decimal";
    private const string CommercialBrainzMigrationKey = "commercial-brainz-v1";
    private const string AiLineupMigrationKey = "ai-lineup-v1";
    private const string AiPlayoutTemplateMigrationKey = "ai-playout-template-v1";
    private const string SpectralTvListsMigrationKey = "spectraltv-lists-v1";
    private const string SpecialPresentationMigrationKey = "special-presentation-v1";
    private const string WeatherLocationMigrationKey = "weather-location-query-v1";
    private static readonly Regex SqlIdentifierRegex = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    public static async Task MigrateAsync(SpectralTvDbContext db, ILogger logger, CancellationToken cancellationToken)
    {
        await db.Database.EnsureCreatedAsync(cancellationToken);
        await EnsureSchemaTableAsync(db, cancellationToken);

        if (!await IsMigrationAppliedAsync(db, ChannelNumberMigrationKey, cancellationToken))
        {
            await MigrateChannelNumberColumnAsync(db, logger, cancellationToken);
            await MarkMigrationAppliedAsync(db, ChannelNumberMigrationKey, cancellationToken);
        }

        if (!await IsMigrationAppliedAsync(db, CommercialBrainzMigrationKey, cancellationToken))
        {
            await MigrateCommercialBrainzAsync(db, logger, cancellationToken);
            await MarkMigrationAppliedAsync(db, CommercialBrainzMigrationKey, cancellationToken);
        }

        if (!await IsMigrationAppliedAsync(db, AiLineupMigrationKey, cancellationToken))
        {
            await MigrateAiLineupAsync(db, logger, cancellationToken);
            await MarkMigrationAppliedAsync(db, AiLineupMigrationKey, cancellationToken);
        }

        if (!await IsMigrationAppliedAsync(db, AiPlayoutTemplateMigrationKey, cancellationToken))
        {
            await MigrateAiPlayoutTemplateAsync(db, logger, cancellationToken);
            await MarkMigrationAppliedAsync(db, AiPlayoutTemplateMigrationKey, cancellationToken);
        }

        if (!await IsMigrationAppliedAsync(db, SpectralTvListsMigrationKey, cancellationToken))
        {
            await MigrateSpectralTvListsAsync(db, logger, cancellationToken);
            await MarkMigrationAppliedAsync(db, SpectralTvListsMigrationKey, cancellationToken);
        }

        if (!await IsMigrationAppliedAsync(db, SpecialPresentationMigrationKey, cancellationToken))
        {
            await MigrateSpecialPresentationsAsync(db, logger, cancellationToken);
            await MarkMigrationAppliedAsync(db, SpecialPresentationMigrationKey, cancellationToken);
        }

        if (!await IsMigrationAppliedAsync(db, WeatherLocationMigrationKey, cancellationToken))
        {
            await MigrateWeatherLocationQueryAsync(db, logger, cancellationToken);
            await MarkMigrationAppliedAsync(db, WeatherLocationMigrationKey, cancellationToken);
        }
    }

    private static Task EnsureSchemaTableAsync(SpectralTvDbContext db, CancellationToken cancellationToken)
    {
        return db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "__SpectralTvSchema" (
                "Key" TEXT NOT NULL PRIMARY KEY,
                "AppliedAt" TEXT NOT NULL
            );
            """,
            cancellationToken);
    }

    private static async Task<bool> IsMigrationAppliedAsync(
        SpectralTvDbContext db,
        string migrationKey,
        CancellationToken cancellationToken)
    {
        var applied = await db.Database
            .SqlQueryRaw<long>("SELECT COUNT(*) AS \"Value\" FROM \"__SpectralTvSchema\" WHERE \"Key\" = {0}", migrationKey)
            .FirstAsync(cancellationToken);

        return applied > 0;
    }

    private static async Task MigrateChannelNumberColumnAsync(
        SpectralTvDbContext db,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var tableExists = await db.Database
            .SqlQueryRaw<long>("SELECT COUNT(*) AS \"Value\" FROM sqlite_master WHERE type = 'table' AND name = 'Channels'")
            .FirstAsync(cancellationToken);

        if (tableExists == 0)
        {
            return;
        }

        var numberType = await GetColumnTypeAsync(db, "Channels", "Number", cancellationToken);
        var hasNumber = await ColumnExistsAsync(db, "Channels", "Number", cancellationToken);
        var hasNumberReal = await ColumnExistsAsync(db, "Channels", "NumberReal", cancellationToken);

        if (hasNumber && IsDecimalColumnType(numberType))
        {
            await EnsureChannelNumberIndexAsync(db, cancellationToken);
            return;
        }

        if (hasNumberReal)
        {
            logger.LogInformation("Resuming Channels.Number decimal migration");
            await DropChannelNumberIndexAsync(db, cancellationToken);

            if (hasNumber)
            {
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE \"Channels\" SET \"NumberReal\" = CAST(\"Number\" AS REAL)",
                    cancellationToken);
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Channels\" DROP COLUMN \"Number\"", cancellationToken);
            }

            if (await ColumnExistsAsync(db, "Channels", "NumberReal", cancellationToken))
            {
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE \"Channels\" RENAME COLUMN \"NumberReal\" TO \"Number\"",
                    cancellationToken);
            }

            await EnsureChannelNumberIndexAsync(db, cancellationToken);
            return;
        }

        if (!hasNumber)
        {
            return;
        }

        logger.LogInformation("Migrating Channels.Number to REAL for sub-channel numbers");

        await DropChannelNumberIndexAsync(db, cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"Channels\" ADD COLUMN \"NumberReal\" REAL NOT NULL DEFAULT 1",
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE \"Channels\" SET \"NumberReal\" = CAST(\"Number\" AS REAL)",
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Channels\" DROP COLUMN \"Number\"", cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"Channels\" RENAME COLUMN \"NumberReal\" TO \"Number\"",
            cancellationToken);
        await EnsureChannelNumberIndexAsync(db, cancellationToken);
    }

    private static async Task MigrateCommercialBrainzAsync(
        SpectralTvDbContext db,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(db, "Commercials", cancellationToken))
        {
            return;
        }

        logger.LogInformation("Applying CommercialBrainz schema migration");

        await AddColumnIfMissingAsync(db, "Commercials", "Source", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await AddColumnIfMissingAsync(db, "Commercials", "YouTubeUrl", "TEXT", cancellationToken);
        await AddColumnIfMissingAsync(db, "Commercials", "YouTubeVideoId", "TEXT", cancellationToken);
        await AddColumnIfMissingAsync(db, "Commercials", "CommercialBrainzVideoSbid", "TEXT", cancellationToken);
        await AddColumnIfMissingAsync(db, "Commercials", "Brand", "TEXT", cancellationToken);
        await AddColumnIfMissingAsync(db, "Commercials", "Year", "INTEGER", cancellationToken);
        await AddColumnIfMissingAsync(db, "Commercials", "Decade", "INTEGER", cancellationToken);
        await AddColumnIfMissingAsync(db, "Commercials", "Network", "TEXT", cancellationToken);
        await AddColumnIfMissingAsync(db, "Commercials", "ChannelName", "TEXT", cancellationToken);
        await AddColumnIfMissingAsync(db, "Commercials", "AgeLimit", "INTEGER", cancellationToken);
        await AddColumnIfMissingAsync(db, "Commercials", "TagsJson", "TEXT", cancellationToken);
        await AddColumnIfMissingAsync(db, "Commercials", "IsBanned", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await AddColumnIfMissingAsync(db, "Commercials", "IsAdultRated", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await AddColumnIfMissingAsync(db, "Commercials", "IsLateNight", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await AddColumnIfMissingAsync(db, "Commercials", "IsSpoof", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await AddColumnIfMissingAsync(db, "Commercials", "IsFake", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await AddColumnIfMissingAsync(db, "Commercials", "IsReal", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await AddColumnIfMissingAsync(db, "Commercials", "IsAiEnhanced", "INTEGER NOT NULL DEFAULT 0", cancellationToken);

        await db.Database.ExecuteSqlRawAsync("DROP INDEX IF EXISTS \"IX_Commercials_JellyfinItemId\";", cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE INDEX IF NOT EXISTS "IX_Commercials_JellyfinItemId" ON "Commercials" ("JellyfinItemId");
            """,
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Commercials_CommercialBrainzVideoSbid"
            ON "Commercials" ("CommercialBrainzVideoSbid")
            WHERE "CommercialBrainzVideoSbid" IS NOT NULL;
            """,
            cancellationToken);

        if (await TableExistsAsync(db, "PlayoutItems", cancellationToken))
        {
            await AddColumnIfMissingAsync(db, "PlayoutItems", "CommercialId", "TEXT", cancellationToken);
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE INDEX IF NOT EXISTS "IX_PlayoutItems_CommercialId" ON "PlayoutItems" ("CommercialId");
                """,
                cancellationToken);
        }
    }

    private static async Task MigrateAiLineupAsync(
        SpectralTvDbContext db,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Applying AI lineup schema migration");

        if (await TableExistsAsync(db, "Channels", cancellationToken))
        {
            await AddColumnIfMissingAsync(db, "Channels", "CatalogMode", "INTEGER", cancellationToken);
            await AddColumnIfMissingAsync(db, "Channels", "AiFineTunePrompt", "TEXT", cancellationToken);
        }

        if (await TableExistsAsync(db, "LineupSlots", cancellationToken))
        {
            await AddColumnIfMissingAsync(db, "LineupSlots", "SpanSlots", "INTEGER NOT NULL DEFAULT 1", cancellationToken);
        }
    }

    private static async Task MigrateAiPlayoutTemplateAsync(
        SpectralTvDbContext db,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Applying AI playout template schema migration");

        if (await TableExistsAsync(db, "Channels", cancellationToken))
        {
            await AddColumnIfMissingAsync(db, "Channels", "AiPlayoutTemplateId", "TEXT", cancellationToken);
        }
    }

    private static async Task MigrateSpectralTvListsAsync(
        SpectralTvDbContext db,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Applying SpectralTV lists schema migration");

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "SpectralTvLists" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "Name" TEXT NOT NULL,
                "JellyfinPlaylistId" TEXT NOT NULL,
                "PlaybackMode" INTEGER NOT NULL DEFAULT 0,
                "CreatedAt" TEXT NOT NULL
            );
            """,
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_SpectralTvLists_JellyfinPlaylistId"
            ON "SpectralTvLists" ("JellyfinPlaylistId");
            """,
            cancellationToken);

        if (await TableExistsAsync(db, "SlotCandidates", cancellationToken))
        {
            await AddColumnIfMissingAsync(db, "SlotCandidates", "SpectralTvListId", "TEXT", cancellationToken);
        }
    }

    private static async Task MigrateSpecialPresentationsAsync(
        SpectralTvDbContext db,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Applying special presentation schema migration");

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "SpecialPresentations" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "ChannelId" TEXT NOT NULL,
                "Name" TEXT NOT NULL,
                "Enabled" INTEGER NOT NULL DEFAULT 1,
                "DayOfWeek" INTEGER NOT NULL,
                "SlotIndex" INTEGER NOT NULL,
                "SpanSlots" INTEGER NOT NULL DEFAULT 1
            );
            """,
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "SpecialPresentationCandidates" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "SpecialPresentationId" TEXT NOT NULL,
                "Kind" INTEGER NOT NULL,
                "JellyfinItemId" TEXT,
                "CollectionName" TEXT,
                "FilterJson" TEXT,
                "SpectralTvListId" TEXT,
                "Weight" INTEGER NOT NULL DEFAULT 1,
                "SortOrder" INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY("SpecialPresentationId") REFERENCES "SpecialPresentations"("Id") ON DELETE CASCADE
            );
            """,
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE INDEX IF NOT EXISTS "IX_SpecialPresentations_ChannelId_DayOfWeek_SlotIndex"
            ON "SpecialPresentations" ("ChannelId", "DayOfWeek", "SlotIndex");
            """,
            cancellationToken);
    }

    private static async Task MigrateWeatherLocationQueryAsync(
        SpectralTvDbContext db,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(db, "Channels", cancellationToken))
        {
            return;
        }

        logger.LogInformation("Applying weather location query schema migration");

        await AddColumnIfMissingAsync(db, "Channels", "WeatherLocationQuery", "TEXT", cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE "Channels"
            SET "WeatherLocationQuery" = '50317, Des Moines, IA, USA'
            WHERE "ContentType" = 4
              AND ("WeatherLocationQuery" IS NULL OR TRIM("WeatherLocationQuery") = '');
            """,
            cancellationToken);
    }

    private static async Task AddColumnIfMissingAsync(
        SpectralTvDbContext db,
        string table,
        string column,
        string definition,
        CancellationToken cancellationToken)
    {
        if (await ColumnExistsAsync(db, table, column, cancellationToken))
        {
            return;
        }

        ValidateSqlIdentifier(table);
        ValidateSqlIdentifier(column);
        ValidateColumnDefinition(definition);

        var sql = "ALTER TABLE \"" + table + "\" ADD COLUMN \"" + column + "\" " + definition + ";";
        await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
    }

    private static void ValidateSqlIdentifier(string value)
    {
        if (string.IsNullOrEmpty(value) || !SqlIdentifierRegex.IsMatch(value))
        {
            throw new InvalidOperationException("Invalid SQL identifier.");
        }
    }

    private static void ValidateColumnDefinition(string definition)
    {
        if (string.IsNullOrWhiteSpace(definition)
            || definition.Contains(';', StringComparison.Ordinal)
            || definition.Contains('"', StringComparison.Ordinal)
            || definition.Contains('\'', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Invalid SQL column definition.");
        }
    }

    private static async Task<bool> TableExistsAsync(SpectralTvDbContext db, string table, CancellationToken cancellationToken)
    {
        var count = await db.Database
            .SqlQueryRaw<long>(
                "SELECT COUNT(*) AS \"Value\" FROM sqlite_master WHERE type = 'table' AND name = {0}",
                table)
            .FirstAsync(cancellationToken);

        return count > 0;
    }

    private static Task DropChannelNumberIndexAsync(SpectralTvDbContext db, CancellationToken cancellationToken)
    {
        return db.Database.ExecuteSqlRawAsync(
            """
            DROP INDEX IF EXISTS "IX_Channels_Number";
            """,
            cancellationToken);
    }

    private static Task EnsureChannelNumberIndexAsync(SpectralTvDbContext db, CancellationToken cancellationToken)
    {
        return db.Database.ExecuteSqlRawAsync(
            """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Channels_Number" ON "Channels" ("Number");
            """,
            cancellationToken);
    }

    private static async Task<bool> ColumnExistsAsync(
        SpectralTvDbContext db,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        var count = await db.Database
            .SqlQueryRaw<long>(
                "SELECT COUNT(*) AS \"Value\" FROM pragma_table_info({0}) WHERE name = {1}",
                table,
                column)
            .FirstAsync(cancellationToken);

        return count > 0;
    }

    private static async Task<string?> GetColumnTypeAsync(
        SpectralTvDbContext db,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        if (!await ColumnExistsAsync(db, table, column, cancellationToken))
        {
            return null;
        }

        return await db.Database
            .SqlQueryRaw<string>(
                "SELECT type AS \"Value\" FROM pragma_table_info({0}) WHERE name = {1} LIMIT 1",
                table,
                column)
            .FirstAsync(cancellationToken);
    }

    private static bool IsDecimalColumnType(string? columnType)
    {
        return columnType is not null
            && (columnType.Contains("REAL", StringComparison.OrdinalIgnoreCase)
                || columnType.Contains("NUMERIC", StringComparison.OrdinalIgnoreCase)
                || columnType.Contains("DECIMAL", StringComparison.OrdinalIgnoreCase));
    }

    private static Task MarkMigrationAppliedAsync(SpectralTvDbContext db, string migrationKey, CancellationToken cancellationToken)
    {
        return db.Database.ExecuteSqlRawAsync(
            """
            INSERT OR IGNORE INTO "__SpectralTvSchema" ("Key", "AppliedAt")
            VALUES ({0}, {1});
            """,
            new object[] { migrationKey, DateTime.UtcNow.ToString("O") },
            cancellationToken);
    }
}
