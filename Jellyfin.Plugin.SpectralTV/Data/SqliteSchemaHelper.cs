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

        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await using var existsCommand = connection.CreateCommand();
            existsCommand.CommandText = $"SELECT COUNT(*) FROM pragma_table_info(\"{table}\") WHERE name = $column;";
            var columnParameter = existsCommand.CreateParameter();
            columnParameter.ParameterName = "$column";
            columnParameter.Value = column;
            existsCommand.Parameters.Add(columnParameter);

            var scalar = await existsCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (Convert.ToInt64(scalar, System.Globalization.CultureInfo.InvariantCulture) > 0)
            {
                return;
            }

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
