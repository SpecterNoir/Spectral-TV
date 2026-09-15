using System.Reflection;
using Jellyfin.Plugin.SpectralTV.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

var databasePath = Path.Combine(Path.GetTempPath(), $"spectraltv-upgrade-{Guid.NewGuid():N}.db");
try
{
    var options = new DbContextOptionsBuilder<SpectralTvDbContext>()
        .UseSqlite($"Data Source={databasePath}")
        .Options;

    await using var db = new SpectralTvDbContext(options);

    // Reproduce the important pieces of a real upgraded installation instead of testing only a
    // pristine database. NumberReal represents a migration that was interrupted after adding the
    // temporary column, while ChannelProgrammingSettings represents the pre-SelectionMode schema.
    await db.Database.ExecuteSqlRawAsync(
        """
        CREATE TABLE "Channels" (
            "Id" TEXT NOT NULL PRIMARY KEY,
            "Number" INTEGER NOT NULL,
            "NumberReal" REAL NOT NULL DEFAULT 1
        );

        CREATE TABLE "ChannelProgrammingSettings" (
            "ChannelId" TEXT NOT NULL PRIMARY KEY,
            "Enabled" INTEGER NOT NULL DEFAULT 0,
            "FillerEnabled" INTEGER NOT NULL DEFAULT 1,
            "FillerChancePercent" INTEGER NOT NULL DEFAULT 100,
            "MinFillerItems" INTEGER NOT NULL DEFAULT 1,
            "MaxFillerItems" INTEGER NOT NULL DEFAULT 2,
            "MaxFillerSeconds" INTEGER NOT NULL DEFAULT 180,
            "FillerRepeatWindow" INTEGER NOT NULL DEFAULT 12
        );

        CREATE TABLE "PlayoutItems" (
            "Id" TEXT NOT NULL PRIMARY KEY
        );

        CREATE TABLE "PlayoutHistory" (
            "Id" TEXT NOT NULL PRIMARY KEY
        );
        """);

    await InvokeMigrationAsync("SchemaMigrator", db);
    await InvokeMigrationAsync("WeightedProgrammingSchemaMigrator", db);
    await InvokeMigrationAsync("OnDemandSchemaMigrator", db);

    await AssertTableAsync(db, "ChannelProgrammingSettings");
    await AssertColumnAsync(db, "ChannelProgrammingSettings", "SelectionMode");
    await AssertTableAsync(db, "ChannelProgramSources");
    await AssertTableAsync(db, "ChannelFillerSources");
    await AssertColumnAsync(db, "PlayoutItems", "ProgramSourceId");
    await AssertColumnAsync(db, "PlayoutHistory", "ProgramSourceId");
    await AssertTableAsync(db, "OnDemandChannels");
    await AssertTableAsync(db, "OnDemandSources");
    await AssertTableAsync(db, "OnDemandFillerSources");
    await AssertTableAsync(db, "OnDemandProgress");
    await AssertTableAsync(db, "OnDemandPlaylistLinks");

    // The repair must not destructively rewrite a user's working Channels table merely because
    // an old temporary NumberReal column is present.
    await AssertColumnAsync(db, "Channels", "Number");
    await AssertColumnAsync(db, "Channels", "NumberReal");

    Console.WriteLine("Spectral TV legacy schema upgrade probe passed.");
}
finally
{
    try { File.Delete(databasePath); } catch { }
}

static async Task InvokeMigrationAsync(string typeName, SpectralTvDbContext db)
{
    var type = typeof(SpectralTvDbContext).Assembly.GetType($"Jellyfin.Plugin.SpectralTV.Data.{typeName}", throwOnError: true)!;
    var method = type.GetMethod("MigrateAsync", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException($"{typeName}.MigrateAsync was not found.");
    var result = method.Invoke(null, new object[] { db, NullLogger.Instance, CancellationToken.None })
        ?? throw new InvalidOperationException($"{typeName}.MigrateAsync returned null.");
    await ((Task)result).ConfigureAwait(false);
}

static async Task AssertTableAsync(SpectralTvDbContext db, string table)
{
    var count = await db.Database.SqlQueryRaw<long>(
        "SELECT COUNT(*) AS \"Value\" FROM sqlite_master WHERE type = 'table' AND name = {0}",
        table).FirstAsync();
    if (count != 1)
    {
        throw new InvalidOperationException($"Required repaired table '{table}' was not created.");
    }
}

static async Task AssertColumnAsync(SpectralTvDbContext db, string table, string column)
{
    var count = await db.Database.SqlQueryRaw<long>(
        "SELECT COUNT(*) AS \"Value\" FROM pragma_table_info({0}) WHERE name = {1}",
        table,
        column).FirstAsync();
    if (count != 1)
    {
        throw new InvalidOperationException($"Required repaired column '{table}.{column}' was not created.");
    }
}
