using Microsoft.Data.Sqlite;
using System;
using System.IO;
using TFM.Nucleus.Data.Providers;

namespace TFM.Nucleus.Data.SQLite
{
    /// <summary>
    /// The SQLite provider's settings: the shared relational settings plus what only SQLite needs.
    /// <see cref="RelationalProviderOptions.Database"/> is the path of the database file, created
    /// with its folder when it does not exist and the schema is writable; a relative path is
    /// resolved against the process's current directory. Server and Username mean nothing to
    /// SQLite and are ignored. Password is refused: the bundled SQLite does not encrypt, and
    /// ignoring it would leave the file readable by anyone who can open it.
    /// <para>
    /// The file belongs on a local disk. Write-ahead logging, and SQLite's locking in general, is
    /// not safe on a network share.
    /// </para>
    /// </summary>
    public class SqliteConfiguration : RelationalProviderOptions
    {
        // SQLite itself takes 32766 parameters per statement, but a Nucleus write is one command of
        // many statements, and the driver binds each statement by searching every parameter of the
        // command, so a command's cost grows with statements times parameters. Measured on an
        // oversized save: 33 s at 30000 a command, 0.3 s at 2000, while the write lock is held
        // throughout. A delete binds up to 1000 IDs a statement, so the limit cannot go much lower.
        public SqliteConfiguration() : base(maxParametersPerQuery: 2000)
        {
        }

        /// <summary>
        /// Puts the database in write-ahead log mode when the provider starts, so reads run while
        /// one connection writes. On by default. Off leaves the file's journal mode as it is.
        /// </summary>
        public bool UseWriteAheadLog { get; set; } = true;

        private int _busyTimeoutSeconds = 3;

        /// <summary>
        /// How long a command waits for another connection's write lock, in seconds, before it
        /// fails. SQLite lets one connection write at a time; every other writer, in this process or
        /// another, waits up to this long. 3 by default: the failure goes back to the caller, who
        /// decides whether to wait longer or retry. An application that writes heavily from
        /// several connections raises it. A wait that runs out fails with a transient
        /// <see cref="DatabaseException"/> of kind <see cref="DatabaseErrorKind.Timeout"/>.
        /// <para>
        /// It is the only time limit SQLite has. The driver's command timeout is this wait for the
        /// lock, not a limit on a running statement, so
        /// <see cref="RelationalProviderOptions.StatementTimeoutSeconds"/> is ignored here: a
        /// statement runs until it finishes, and a cancelled token does not stop one under way.
        /// </para>
        /// </summary>
        public int BusyTimeoutSeconds
        {
            get => _busyTimeoutSeconds;
            set
            {
                ArgumentOutOfRangeException.ThrowIfNegative(value);
                _busyTimeoutSeconds = value;
            }
        }

        /// <summary>The full path of the database file: <see cref="RelationalProviderOptions.Database"/>, or else the connection string's data source.</summary>
        internal string FilePath
        {
            get
            {
                var path = Database;
                if (string.IsNullOrWhiteSpace(path) && ConnectionString != null)
                    path = new SqliteConnectionStringBuilder(ConnectionString).DataSource;
                if (string.IsNullOrWhiteSpace(path))
                    throw new SqliteDataProviderException("The SQLite database file is missing: set Database to its path.", DataProviderErrorKind.NotConfigured);
                if (path == ":memory:" || path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                    throw new SqliteDataProviderException(
                        $"'{path}' is not a database file. The SQLite provider needs a file path: an in-memory database ends with its connection, and Nucleus closes a connection after each read.",
                        DataProviderErrorKind.NotConfigured);
                return Path.GetFullPath(path);
            }
        }

        /// <summary>
        /// A closed connection to the database file, with Nucleus's functions registered. Only
        /// <paramref name="create"/> may create the file: every other connection fails on a missing
        /// file instead of starting an empty database in its place.
        /// </summary>
        internal SqliteConnection BuildConnection(bool create = false)
        {
            if (!string.IsNullOrEmpty(Password))
                throw new SqliteDataProviderException(
                    "A password is set, but the bundled SQLite does not encrypt the database. Remove the password, or use an encrypted SQLite build through ConnectionString.",
                    DataProviderErrorKind.NotConfigured);
            var builder = ConnectionString != null ? new SqliteConnectionStringBuilder(ConnectionString) : new SqliteConnectionStringBuilder();
            builder.DataSource = FilePath;
            builder.Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite;
            builder.DefaultTimeout = BusyTimeoutSeconds;
            if (ConnectionString == null)
            {
                builder.Pooling = true;
                builder.Cache = SqliteCacheMode.Default;
            }
            var connection = new SqliteConnection(builder.ConnectionString);
            SqliteFunctions.Register(connection);
            return connection;
        }
    }
}
