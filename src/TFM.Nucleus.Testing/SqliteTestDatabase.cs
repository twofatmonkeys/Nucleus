using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TFM.Nucleus.Data.Providers;

namespace TFM.Nucleus.Testing;

/// <summary>
/// The SQLite test database: a file per .NET runtime, at the path
/// <see cref="NucleusTestDatabaseSettings.Database"/> holds with the runtime's major version added
/// before the extension (<c>nucleus.db</c> becomes <c>nucleus.net10.db</c>). The provider creates it
/// when it is missing. Unlike the server databases, the net8.0 and net10.0 runs do not share one:
/// SQLite lets one connection write at a time and does not queue its waiters, so two runs writing
/// one file back to back could starve a writer past its busy timeout. Writers inside one run still
/// contend, which the provider's own tests exercise.
/// </summary>
public sealed class SqliteTestDatabase : NucleusTestDatabase
{
    /// <param name="settings">Where the database is; null when it is not configured.</param>
    public SqliteTestDatabase(NucleusTestDatabaseSettings? settings) : base(settings)
    {
    }

    /// <inheritdoc />
    public override string ProviderName => "sqlite";

    /// <inheritdoc />
    protected override string ConfigurationHint => "set the Nucleus:Sqlite:Database user secret or the NUCLEUS_SQLITE_TEST_DATABASE environment variable to a file path.";

    /// <inheritdoc />
    public override void AddProvider(IServiceCollection services, Action<RelationalProviderOptions> configure) =>
        services.AddNucleusSqliteDataProvider(options => UseTestFile(options, configure));

    /// <inheritdoc />
    public override void AddRootedProvider(IServiceCollection services, Type rootType, Action<RelationalProviderOptions> configure) =>
        services.AddNucleusSqliteDataProvider(rootType, options => UseTestFile(options, configure));

    /// <summary>A stack with its SQLite settings changed by <paramref name="configure"/>.</summary>
    public Task<NucleusTestStack> StartStackAsync(Action<TFM.Nucleus.Data.SQLite.SqliteConfiguration> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return StartStackAsync((RelationalProviderOptions options) => configure((TFM.Nucleus.Data.SQLite.SqliteConfiguration)options));
    }

    /// <inheritdoc />
    public override async Task ExecuteAsync(string sql, CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        await using var command = new SqliteCommand(sql, connection);
        await command.ExecuteNonQueryAsync(token);
    }

    /// <inheritdoc />
    public override async Task<object?> ScalarAsync(string sql, CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        await using var command = new SqliteCommand(sql, connection);
        var value = await command.ExecuteScalarAsync(token);
        return value is DBNull ? null : value;
    }

    /// <inheritdoc />
    /// <remarks>
    /// SQLite has no table locks: an IMMEDIATE transaction takes the file's write lock, so every
    /// other writer waits, whatever table it writes, and readers carry on in write-ahead log mode.
    /// </remarks>
    public override async Task<IAsyncDisposable> HoldWriteLockAsync(string table, CancellationToken token = default)
    {
        var connection = await OpenAsync(token);
        try
        {
            var transaction = connection.BeginTransaction(deferred: false);
            return new HeldLock(connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <inheritdoc />
    /// <remarks>The table's root page: a rename keeps it, a rebuild creates the table anew on another page.</remarks>
    public override string TableIdSql(string table) => $"SELECT rootpage FROM sqlite_schema WHERE type = 'table' AND name = '{table.Replace("'", "''")}'";

    /// <inheritdoc />
    public override string DeleteAllSql(string table) => $"DELETE FROM \"{table}\"";

    /// <inheritdoc />
    /// <remarks>
    /// SQLite refuses to drop an indexed column, so the column's Nucleus indexes are dropped first,
    /// one statement per index and per column.
    /// </remarks>
    public override string DropColumnsSql(string table, params string[] columns) =>
        string.Join("; ", columns.SelectMany(c => new[]
        {
            $"DROP INDEX IF EXISTS \"NCI_{table}_{c}\"",
            $"DROP INDEX IF EXISTS \"UQ_{table}_{c}\"",
            $"ALTER TABLE \"{table}\" DROP COLUMN \"{c}\"",
        }));

    /// <inheritdoc />
    /// <remarks>
    /// SQLite cannot drop a column that has a foreign key, so a table with one among the columns is
    /// rebuilt, as SQLite's own procedure for a table change describes: on one connection, with
    /// foreign keys off and the legacy rename on (so other tables' <c>REFERENCES</c> to it are left
    /// alone), a table is created from the old definition without the columns, the rows are
    /// copied in rowid order, the old table is dropped, the new one takes its name and the indexes
    /// that do not use the columns are created again, all in one transaction.
    /// </remarks>
    public override async Task DropColumnsAsync(string table, IEnumerable<string> columns, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var dropped = columns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        await using var connection = await OpenAsync(token);
        var keyColumns = await ReadStringsAsync(connection, null, $"SELECT \"from\" FROM pragma_foreign_key_list('{Escape(table)}')", token);
        if (!keyColumns.Any(dropped.Contains))
        {
            await ExecuteOnAsync(connection, null, DropColumnsSql(table, [.. dropped]), token);
            return;
        }

        var foreignKeys = Convert.ToInt64(await ScalarOnAsync(connection, null, "PRAGMA foreign_keys", token), System.Globalization.CultureInfo.InvariantCulture);
        var legacyAlterTable = Convert.ToInt64(await ScalarOnAsync(connection, null, "PRAGMA legacy_alter_table", token), System.Globalization.CultureInfo.InvariantCulture);
        await ExecuteOnAsync(connection, null, "PRAGMA foreign_keys = OFF; PRAGMA legacy_alter_table = ON", token);
        try
        {
            await using var transaction = connection.BeginTransaction(deferred: false);
            var definition = (string?)await ScalarOnAsync(connection, transaction, $"SELECT sql FROM sqlite_schema WHERE type = 'table' AND name = '{Escape(table)}'", token)
                ?? throw new InvalidOperationException($"There is no table '{table}'.");
            var kept = (await ReadStringsAsync(connection, transaction, $"SELECT name FROM pragma_table_info('{Escape(table)}') ORDER BY cid", token))
                .Where(c => !dropped.Contains(c)).ToList();
            var indexes = new List<string>();
            foreach (var (name, sql) in await ReadPairsAsync(connection, transaction, $"SELECT name, sql FROM sqlite_schema WHERE type = 'index' AND tbl_name = '{Escape(table)}' AND sql IS NOT NULL", token))
            {
                var indexed = await ReadStringsAsync(connection, transaction, $"SELECT name FROM pragma_index_info('{Escape(name)}')", token);
                if (!indexed.Any(dropped.Contains))
                    indexes.Add(sql);
            }

            var temp = table + "__drop";
            var list = string.Join(", ", kept.Select(c => $"\"{c}\""));
            await ExecuteOnAsync(connection, transaction, WithoutColumns(definition, temp, dropped), token);
            await ExecuteOnAsync(connection, transaction, $"INSERT INTO \"{temp}\" ({list}) SELECT {list} FROM \"{table}\" ORDER BY rowid", token);
            await ExecuteOnAsync(connection, transaction, $"DROP TABLE \"{table}\"", token);
            await ExecuteOnAsync(connection, transaction, $"ALTER TABLE \"{temp}\" RENAME TO \"{table}\"", token);
            foreach (var sql in indexes)
                await ExecuteOnAsync(connection, transaction, sql, token);
            await transaction.CommitAsync(token);
        }
        finally
        {
            // The connection goes back to the pool with the settings it was opened with.
            await ExecuteOnAsync(connection, null, $"PRAGMA legacy_alter_table = {legacyAlterTable}; PRAGMA foreign_keys = {foreignKeys}", CancellationToken.None);
        }
    }

    /// <summary>
    /// A table's <c>CREATE TABLE</c> text under another name, without the definitions of the named
    /// columns. The column list is split at its top-level commas, so a type's size facets and a
    /// key's <c>REFERENCES ... (...)</c> stay with their column. A table constraint that names a
    /// dropped column is refused, because dropping the column would change what it enforces.
    /// </summary>
    private static string WithoutColumns(string definition, string newName, HashSet<string> dropped)
    {
        var open = definition.IndexOf('(');
        var close = definition.LastIndexOf(')');
        if (open < 0 || close < open)
            throw new InvalidOperationException($"Cannot read the column list of: {definition}");
        var kept = new List<string>();
        foreach (var part in SplitTopLevel(definition[(open + 1)..close]))
        {
            var first = FirstToken(part);
            if (dropped.Contains(first))
                continue;
            if (first.ToUpperInvariant() is "PRIMARY" or "UNIQUE" or "CHECK" or "FOREIGN" or "CONSTRAINT"
                && dropped.Any(c => part.Contains($"\"{c}\"", StringComparison.OrdinalIgnoreCase)))
                throw new NotSupportedException($"The table constraint '{part}' uses a column being dropped.");
            kept.Add(part);
        }
        return $"CREATE TABLE \"{newName}\" ({string.Join(", ", kept)})";
    }

    private static IEnumerable<string> SplitTopLevel(string list)
    {
        var depth = 0;
        var quote = '\0';
        var start = 0;
        for (var i = 0; i < list.Length; i++)
        {
            var c = list[i];
            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
            }
            else if (c is '"' or '\'' or '`' or '[')
                quote = c == '[' ? ']' : c;
            else if (c == '(')
                depth++;
            else if (c == ')')
                depth--;
            else if (c == ',' && depth == 0)
            {
                yield return list[start..i].Trim();
                start = i + 1;
            }
        }
        yield return list[start..].Trim();
    }

    /// <summary>A definition's first word, without the quotes around an identifier.</summary>
    private static string FirstToken(string part)
    {
        if (part.Length > 0 && part[0] is '"' or '`' or '[')
        {
            var end = part[0] == '[' ? ']' : part[0];
            var close = part.IndexOf(end, 1);
            return close < 0 ? part[1..] : part[1..close];
        }
        var space = part.IndexOfAny([' ', '\t', '\r', '\n', '(']);
        return space < 0 ? part : part[..space];
    }

    private static string Escape(string name) => name.Replace("'", "''");

    private static async Task ExecuteOnAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken token)
    {
        await using var command = new SqliteCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<object?> ScalarOnAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken token)
    {
        await using var command = new SqliteCommand(sql, connection, transaction);
        var value = await command.ExecuteScalarAsync(token);
        return value is DBNull ? null : value;
    }

    private static async Task<List<string>> ReadStringsAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken token)
    {
        await using var command = new SqliteCommand(sql, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(token);
        var values = new List<string>();
        while (await reader.ReadAsync(token))
            values.Add(reader.GetString(0));
        return values;
    }

    private static async Task<List<(string Name, string Sql)>> ReadPairsAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken token)
    {
        await using var command = new SqliteCommand(sql, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(token);
        var values = new List<(string, string)>();
        while (await reader.ReadAsync(token))
            values.Add((reader.GetString(0), reader.GetString(1)));
        return values;
    }

    /// <inheritdoc />
    public override string DropTableIfExistsSql(string table) => $"DROP TABLE IF EXISTS \"{table}\"";

    /// <inheritdoc />
    public override string RenameTableSql(string from, string to) => $"ALTER TABLE \"{from}\" RENAME TO \"{to}\"";

    /// <inheritdoc />
    /// <remarks>SQLite gives the copy's columns the affinity of their values, not their declared types.</remarks>
    public override string CopyTableSql(string from, string to) => $"CREATE TABLE \"{to}\" AS SELECT * FROM \"{from}\"";

    /// <inheritdoc />
    public override string RowCountSql(string table) => $"SELECT count(*) FROM \"{table}\"";

    /// <summary>
    /// Points a provider's options at the test file, then applies <paramref name="configure"/>. SQLite
    /// needs the path only, so the server and sign-in settings are left unset.
    /// </summary>
    private void UseTestFile(RelationalProviderOptions options, Action<RelationalProviderOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configure);
        options.Database = FilePath;
        configure(options);
    }

    /// <summary>The full path of this runtime's file: the configured path with <c>.net{major}</c> before its extension.</summary>
    public string FilePath
    {
        get
        {
            var path = Path.GetFullPath(RequireSettings().Database);
            var runtime = $".net{Environment.Version.Major}";
            return Path.Combine(Path.GetDirectoryName(path) ?? "", Path.GetFileNameWithoutExtension(path) + runtime + Path.GetExtension(path));
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken token)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Mode = SqliteOpenMode.ReadWrite,
            DefaultTimeout = 30,
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync(token);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
