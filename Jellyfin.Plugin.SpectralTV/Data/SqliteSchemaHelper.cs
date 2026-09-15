using System.Data;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.SpectralTV.Data;

/// <summary>
/// Small, deliberately strict helper for additive SQLite schema changes.
/// Identifiers and column definitions are validated before they are interpolated into SQL.
/// </summary>
internal static partial class SqliteSchemaHelper
{
    private static readonly HashSet<string> AllowedColumnDefinitions = new(StringComparer.Ordinal)
    {
        "INTEGER NOT NULL DEFAULT 1",
        "TEXT",
        "TEXT NULL"
    };

    public static async Task<bool> TableExistsAsync(
        SpectralTvDbContext db,
        string table,
        CancellationToken cancellationToken)
    {
        ValidateIdentifier(table, nameof(table));
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $table;";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$table";
            parameter.Value = table;
            command.Parameters.Add(parameter);
            var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return Convert.ToInt64(scalar, System.Globalization.CultureInfo.InvariantCulture) > 0;
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    public static async Task<bool> ColumnExistsAsync(
        SpectralTvDbContext db,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        ValidateIdentifier(table, nameof(table));
        ValidateIdentifier(column, nameof(column));
        if (!await TableExistsAsync(db, table, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM pragma_table_info($table) WHERE name = $column;";

            var tableParameter = command.CreateParameter();
            tableParameter.ParameterName = "$table";
            tableParameter.Value = table;
            command.Parameters.Add(tableParameter);

            var columnParameter = command.CreateParameter();
            columnParameter.ParameterName = "$column";
            columnParameter.Value = column;
            command.Parameters.Add(columnParameter);

            var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return Convert.ToInt64(scalar, System.Globalization.CultureInfo.InvariantCulture) > 0;
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    public static async Task AddColumnIfMissingAsync(
        SpectralTvDbContext db,
        string table,
        string column,
        string definition,
        CancellationToken cancellationToken)
    {
        ValidateIdentifier(table, nameof(table));
        ValidateIdentifier(column, nameof(column));
        if (!AllowedColumnDefinitions.Contains(definition))
        {
            throw new ArgumentException("Unsupported SQLite column definition.", nameof(definition));
        }

        // A very old or partially recovered database can be missing an optional base table entirely.
        // An additive migration must not abort every later schema family just because there is nothing
        // to alter yet. The owning base feature can recreate that table independently.
        if (!await TableExistsAsync(db, table, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (await ColumnExistsAsync(db, table, column, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await using var alterCommand = connection.CreateCommand();
            alterCommand.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition};";
            await alterCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    private static void ValidateIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || !SafeIdentifierRegex().IsMatch(value))
        {
            throw new ArgumentException("SQLite identifier contains unsupported characters.", parameterName);
        }
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeIdentifierRegex();
}
