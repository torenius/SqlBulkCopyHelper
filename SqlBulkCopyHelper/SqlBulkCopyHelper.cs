using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using static SqlBulkCopyHelper.SqlExecution;
using static SqlBulkCopyHelper.SqlNames;

namespace SqlBulkCopyHelper;

/// <summary>
/// Maps entities to columns and bulk inserts them with SqlBulkCopy, by streaming them through a DbDataReader.
/// </summary>
/// <typeparam name="TEntity">The type of the entities to insert</typeparam>
public class SqlBulkCopyHelper<TEntity>
{
    private readonly string _tableName;
    private readonly List<string> _tableNameParts;
    private readonly List<DisguisedColumnDefinition<TEntity>> _columnDefinitions = [];

    /// <summary>
    /// Helper class easier and more memory efficient do a bulk insert of x amount of rows.
    /// You need to call .Map or other methods to map what values should be inserted and to what column names
    /// </summary>
    /// <param name="tableName">The table to insert to. Could be the main table or a staging/temp table.
    /// Can be a multipart name like "dbo.Table", and parts can already be quoted like "[dbo].[My.Table]".</param>
    /// <exception cref="ArgumentNullException">If table name is null or empty it will throw</exception>
    /// <exception cref="ArgumentException">If table name has a quoted part that is not terminated, like "[dbo.Table"</exception>
    public SqlBulkCopyHelper(string tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName)) throw new ArgumentNullException(nameof(tableName));
        _tableName = tableName;
        _tableNameParts = SplitMultipartName(tableName);
    }

    /// <summary>
    /// If you need to convert your list of entities to a DataReader
    /// </summary>
    /// <param name="entities">Entities to convert</param>
    /// <returns>DbDataReader that contains the mapped columns</returns>
    [OverloadResolutionPriority(1)]
    public DbDataReader GetDataReader(IEnumerable<TEntity> entities) =>
        new DisguisedDataReader<TEntity>(_columnDefinitions, entities);

    /// <summary>
    /// If you need to convert your async stream of entities to a DataReader.
    /// The reader must be read with ReadAsync, the synchronous Read throws NotSupportedException.
    /// Dispose it with DisposeAsync, so the source is disposed without blocking.
    /// </summary>
    /// <param name="entities">Entities to convert</param>
    /// <param name="cancellationToken">Passed to the source when it's enumerated</param>
    /// <returns>DbDataReader that contains the mapped columns</returns>
    public DbDataReader GetDataReader(IAsyncEnumerable<TEntity> entities, CancellationToken cancellationToken = default) =>
        new AsyncDisguisedDataReader<TEntity>(_columnDefinitions, entities, cancellationToken);

    /// <summary>
    /// If you need to convert your list of entities to a DataTable
    /// </summary>
    /// <param name="entities">Entities to convert</param>
    /// <returns>DataTable that contains the mapped columns</returns>
    public DataTable GetDataTable(IEnumerable<TEntity> entities)
    {
        var dt = new DataTable(_tableName);
        dt.Load(GetDataReader(entities));
        return dt;
    }

    /// <summary>
    /// Helper method to make the call to SqlBulkCopy easier.
    /// You can do this step yourself by just getting the DataReader from this class.
    /// </summary>
    /// <param name="connection">SqlConnection to connect to. If it's closed, this code will open it, do the insert then close it. If it was open it will be kept open.</param>
    /// <param name="entities">All the entities that will be inserted</param>
    /// <param name="createTableIfNotExists">If true it will first make a call to creating the table that "CreateTableScript" generates. The CREATE TABLE runs in the same transaction as the bulk insert.
    /// If no sqlTransaction is provided, a transaction is started and committed by this method (unless SqlBulkCopyOptions.UseInternalTransaction is used).</param>
    /// <param name="timeout">Number of seconds for the operation to complete before it times out. 0 equals no timeout. Default 30 seconds</param>
    /// <param name="sqlBulkCopyOptions">Different options that SqlBulkCopy will consider</param>
    /// <param name="sqlTransaction">If this should be done in a specific transaction or not. The caller is responsible for commit or rollback.</param>
    /// <param name="cancellationToken">Do you like to have the option to cancel the operation?</param>
    /// <returns>Number of rows inserted</returns>
    // Preferred over the IAsyncEnumerable overload for types that implement both, like an EF Core DbSet
    [OverloadResolutionPriority(1)]
    public ValueTask<long> BulkInsertAsync(SqlConnection connection, IEnumerable<TEntity> entities, bool createTableIfNotExists = false,
        int timeout = 30, SqlBulkCopyOptions sqlBulkCopyOptions = SqlBulkCopyOptions.Default, SqlTransaction? sqlTransaction = null, CancellationToken cancellationToken = default)
    {
        return BulkInsertCoreAsync(connection, (columnDefinitions, onRowRead) => new DisguisedDataReader<TEntity>(columnDefinitions, entities, onRowRead),
            createTableIfNotExists, timeout, sqlBulkCopyOptions, sqlTransaction, cancellationToken);
    }

    /// <summary>
    /// Same as the IEnumerable overload, but streams the entities from an IAsyncEnumerable without blocking threads.
    /// For example EF Core's AsAsyncEnumerable() or Dapper's QueryUnbufferedAsync().
    /// A type that implements both IEnumerable and IAsyncEnumerable (like an EF Core DbSet) uses the IEnumerable overload, call .AsAsyncEnumerable() to use this one.
    /// </summary>
    /// <param name="connection">SqlConnection to connect to. If it's closed, this code will open it, do the insert then close it. If it was open it will be kept open.</param>
    /// <param name="entities">All the entities that will be inserted</param>
    /// <param name="createTableIfNotExists">If true it will first make a call to creating the table that "CreateTableScript" generates. The CREATE TABLE runs in the same transaction as the bulk insert.
    /// If no sqlTransaction is provided, a transaction is started and committed by this method (unless SqlBulkCopyOptions.UseInternalTransaction is used).</param>
    /// <param name="timeout">Number of seconds for the operation to complete before it times out. 0 equals no timeout. Default 30 seconds</param>
    /// <param name="sqlBulkCopyOptions">Different options that SqlBulkCopy will consider</param>
    /// <param name="sqlTransaction">If this should be done in a specific transaction or not. The caller is responsible for commit or rollback.</param>
    /// <param name="cancellationToken">Cancels the operation. It's also passed to the source when it's enumerated.</param>
    /// <returns>Number of rows inserted</returns>
    public ValueTask<long> BulkInsertAsync(SqlConnection connection, IAsyncEnumerable<TEntity> entities, bool createTableIfNotExists = false,
        int timeout = 30, SqlBulkCopyOptions sqlBulkCopyOptions = SqlBulkCopyOptions.Default, SqlTransaction? sqlTransaction = null, CancellationToken cancellationToken = default)
    {
        return BulkInsertCoreAsync(connection, (columnDefinitions, onRowRead) => new AsyncDisguisedDataReader<TEntity>(columnDefinitions, entities, cancellationToken, onRowRead),
            createTableIfNotExists, timeout, sqlBulkCopyOptions, sqlTransaction, cancellationToken);
    }

    private async ValueTask<long> BulkInsertCoreAsync(SqlConnection connection, Func<List<DisguisedColumnDefinition<TEntity>>, Action<TEntity>?, DbDataReader> createReader,
        bool createTableIfNotExists, int timeout, SqlBulkCopyOptions sqlBulkCopyOptions, SqlTransaction? sqlTransaction, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_outputColumns.Count > 0 && sqlBulkCopyOptions.HasFlag(SqlBulkCopyOptions.KeepIdentity))
        {
            throw new NotSupportedException("SqlBulkCopyOptions.KeepIdentity can't be combined with OutputColumn.");
        }

        // CREATE TABLE and the bulk insert should succeed or fail together.
        // If the caller provides a transaction, it's up to the caller to commit or rollback.
        var useOwnTransaction = createTableIfNotExists
            && sqlTransaction is null
            && !sqlBulkCopyOptions.HasFlag(SqlBulkCopyOptions.UseInternalTransaction);

        return await ExecuteAsync(connection, sqlTransaction, useOwnTransaction, async transaction =>
        {
            if (createTableIfNotExists)
            {
                await ExecuteNonQueryAsync(connection, transaction, CreateTableScript(checkIfTableExists: true), timeout, cancellationToken).ConfigureAwait(false);
            }

            return _outputColumns.Count == 0
                ? await WriteToServerAsync(connection, transaction, GetTableName(), createReader(_columnDefinitions, null),
                    extraColumnMappings: [], timeout, sqlBulkCopyOptions, cancellationToken).ConfigureAwait(false)
                : await InsertWithOutputAsync(connection, transaction, createReader, timeout, sqlBulkCopyOptions, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Inserts the entities that don't match a row in the table and updates the ones that do, matched on the columns in MatchOn.
    /// <para>
    /// The entities are bulk copied to a staging temp table. Then the matching rows are updated with an UPDATE, and the rest are inserted with a MERGE that never matches,
    /// in the same transaction. If no sqlTransaction is provided, a transaction is started and committed by this method.
    /// The table is locked with UPDLOCK, HOLDLOCK, so concurrent upserts can't insert the same key twice. An index on the MatchOn columns is recommended.
    /// </para>
    /// <para>
    /// OutputColumn values are set on the inserted, updated and unchanged entities. Without OutputColumn the entities are not kept in memory.
    /// Like with OutputColumn the rows are inserted and updated like an ordinary INSERT and UPDATE: triggers fire, constraints are checked and NULL is inserted as NULL.
    /// SqlBulkCopyOptions.KeepIdentity and UseInternalTransaction are not supported.
    /// </para>
    /// </summary>
    /// <param name="connection">SqlConnection to connect to. If it's closed, this code will open it, do the upsert then close it. If it was open it will be kept open.</param>
    /// <param name="entities">All the entities that will be inserted or updated</param>
    /// <param name="configure">Configures the upsert. MatchOn is required.</param>
    /// <param name="timeout">Number of seconds for each step to complete before it times out. 0 equals no timeout. Default 30 seconds</param>
    /// <param name="sqlBulkCopyOptions">Options for the SqlBulkCopy to the staging table</param>
    /// <param name="sqlTransaction">If this should be done in a specific transaction or not. The caller is responsible for commit or rollback.</param>
    /// <param name="cancellationToken">Do you like to have the option to cancel the operation?</param>
    /// <returns>Number of rows inserted, updated and unchanged</returns>
    /// <exception cref="InvalidOperationException">If MatchOn is missing, a column in the options is not mapped, or the source has duplicate keys and DuplicateKeyHandling is Throw</exception>
    // Preferred over the IAsyncEnumerable overload for types that implement both, like an EF Core DbSet
    [OverloadResolutionPriority(1)]
    public ValueTask<SqlBulkUpsertResult> BulkUpsertAsync(SqlConnection connection, IEnumerable<TEntity> entities, Action<SqlBulkUpsertOptions> configure,
        int timeout = 30, SqlBulkCopyOptions sqlBulkCopyOptions = SqlBulkCopyOptions.Default, SqlTransaction? sqlTransaction = null, CancellationToken cancellationToken = default)
    {
        return BulkUpsertCoreAsync(connection, (columnDefinitions, onRowRead) => new DisguisedDataReader<TEntity>(columnDefinitions, entities, onRowRead),
            configure, timeout, sqlBulkCopyOptions, sqlTransaction, cancellationToken);
    }

    /// <summary>
    /// Same as the IEnumerable overload, but streams the entities from an IAsyncEnumerable without blocking threads.
    /// A type that implements both IEnumerable and IAsyncEnumerable (like an EF Core DbSet) uses the IEnumerable overload, call .AsAsyncEnumerable() to use this one.
    /// </summary>
    /// <param name="connection">SqlConnection to connect to. If it's closed, this code will open it, do the upsert then close it. If it was open it will be kept open.</param>
    /// <param name="entities">All the entities that will be inserted or updated</param>
    /// <param name="configure">Configures the upsert. MatchOn is required.</param>
    /// <param name="timeout">Number of seconds for each step to complete before it times out. 0 equals no timeout. Default 30 seconds</param>
    /// <param name="sqlBulkCopyOptions">Options for the SqlBulkCopy to the staging table</param>
    /// <param name="sqlTransaction">If this should be done in a specific transaction or not. The caller is responsible for commit or rollback.</param>
    /// <param name="cancellationToken">Cancels the operation. It's also passed to the source when it's enumerated.</param>
    /// <returns>Number of rows inserted, updated and unchanged</returns>
    /// <exception cref="InvalidOperationException">If MatchOn is missing, a column in the options is not mapped, or the source has duplicate keys and DuplicateKeyHandling is Throw</exception>
    public ValueTask<SqlBulkUpsertResult> BulkUpsertAsync(SqlConnection connection, IAsyncEnumerable<TEntity> entities, Action<SqlBulkUpsertOptions> configure,
        int timeout = 30, SqlBulkCopyOptions sqlBulkCopyOptions = SqlBulkCopyOptions.Default, SqlTransaction? sqlTransaction = null, CancellationToken cancellationToken = default)
    {
        return BulkUpsertCoreAsync(connection, (columnDefinitions, onRowRead) => new AsyncDisguisedDataReader<TEntity>(columnDefinitions, entities, cancellationToken, onRowRead),
            configure, timeout, sqlBulkCopyOptions, sqlTransaction, cancellationToken);
    }

    private async ValueTask<SqlBulkUpsertResult> BulkUpsertCoreAsync(SqlConnection connection, Func<List<DisguisedColumnDefinition<TEntity>>, Action<TEntity>?, DbDataReader> createReader,
        Action<SqlBulkUpsertOptions> configure, int timeout, SqlBulkCopyOptions sqlBulkCopyOptions, SqlTransaction? sqlTransaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configure);
        cancellationToken.ThrowIfCancellationRequested();

        if (sqlBulkCopyOptions.HasFlag(SqlBulkCopyOptions.KeepIdentity))
        {
            throw new NotSupportedException("SqlBulkCopyOptions.KeepIdentity is not supported by BulkUpsertAsync.");
        }

        if (sqlBulkCopyOptions.HasFlag(SqlBulkCopyOptions.UseInternalTransaction))
        {
            throw new NotSupportedException("SqlBulkCopyOptions.UseInternalTransaction is not supported by BulkUpsertAsync, since all steps run in the same transaction.");
        }

        var options = new SqlBulkUpsertOptions();
        configure(options);
        var upsertColumns = GetUpsertColumns(options);

        // The UPDATE and the INSERT should succeed or fail together
        return await ExecuteAsync(connection, sqlTransaction, useOwnTransaction: sqlTransaction is null,
            transaction => UpsertAsync(connection, transaction, createReader, upsertColumns, timeout, sqlBulkCopyOptions, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Replaces all rows in the table with the entities, without readers ever seeing the table empty or half-filled.
    /// <para>
    /// The entities are bulk copied to a new table with the same columns, indexes and constraints, named like "Table_Incoming_20261004153012" (UTC).
    /// Then, in a short transaction, the old rows are switched out to "Table_Outgoing_20261004153012" and the new rows are switched in, with ALTER TABLE SWITCH that only changes metadata.
    /// Both tables are dropped afterwards. If something fails before the swap, the table is unchanged. If the process is killed, the tables can be left behind and dropped.
    /// </para>
    /// <para>
    /// The table keeps its own object, so permissions, triggers and names stay as they are. Triggers don't fire for the new rows.
    /// The swap needs a schema modification lock and waits for readers that hold locks on the table, see SqlBulkReplaceOptions.WaitAtLowPriority.
    /// A SNAPSHOT transaction that started before the swap fails if it reads the table after it.
    /// </para>
    /// </summary>
    /// <param name="connection">SqlConnection to connect to. If it's closed, this code will open it, do the replace then close it. If it was open it will be kept open. It can't have an ongoing transaction.</param>
    /// <param name="entities">All the rows the table should have</param>
    /// <param name="configure">Configures the swap, for example WaitAtLowPriority</param>
    /// <param name="timeout">Number of seconds for each step to complete before it times out, like the bulk copy, creating the indexes and the swap. 0 equals no timeout. Default 30 seconds</param>
    /// <param name="sqlBulkCopyOptions">Options for the SqlBulkCopy to the incoming table. TableLock is always used, since no one else uses that table.</param>
    /// <param name="cancellationToken">Do you like to have the option to cancel the operation?</param>
    /// <returns>Number of rows in the table</returns>
    /// <exception cref="InvalidOperationException">If the table doesn't exist, or a mapped column doesn't exist in it</exception>
    /// <exception cref="NotSupportedException">If the table is a temp table, is referenced by a foreign key, has Change Tracking, is partitioned, temporal or memory-optimized, or OutputColumn is used</exception>
    // Preferred over the IAsyncEnumerable overload for types that implement both, like an EF Core DbSet
    [OverloadResolutionPriority(1)]
    public ValueTask<long> BulkReplaceAsync(SqlConnection connection, IEnumerable<TEntity> entities, Action<SqlBulkReplaceOptions>? configure = null,
        int timeout = 30, SqlBulkCopyOptions sqlBulkCopyOptions = SqlBulkCopyOptions.Default, CancellationToken cancellationToken = default)
    {
        return BulkReplaceCoreAsync(connection, columnDefinitions => new DisguisedDataReader<TEntity>(columnDefinitions, entities),
            configure, timeout, sqlBulkCopyOptions, cancellationToken);
    }

    /// <summary>
    /// Same as the IEnumerable overload, but streams the entities from an IAsyncEnumerable without blocking threads.
    /// A type that implements both IEnumerable and IAsyncEnumerable (like an EF Core DbSet) uses the IEnumerable overload, call .AsAsyncEnumerable() to use this one.
    /// </summary>
    /// <param name="connection">SqlConnection to connect to. If it's closed, this code will open it, do the replace then close it. If it was open it will be kept open. It can't have an ongoing transaction.</param>
    /// <param name="entities">All the rows the table should have</param>
    /// <param name="configure">Configures the swap, for example WaitAtLowPriority</param>
    /// <param name="timeout">Number of seconds for each step to complete before it times out, like the bulk copy, creating the indexes and the swap. 0 equals no timeout. Default 30 seconds</param>
    /// <param name="sqlBulkCopyOptions">Options for the SqlBulkCopy to the incoming table. TableLock is always used, since no one else uses that table.</param>
    /// <param name="cancellationToken">Cancels the operation. It's also passed to the source when it's enumerated.</param>
    /// <returns>Number of rows in the table</returns>
    /// <exception cref="InvalidOperationException">If the table doesn't exist, or a mapped column doesn't exist in it</exception>
    /// <exception cref="NotSupportedException">If the table is a temp table, is referenced by a foreign key, has Change Tracking, is partitioned, temporal or memory-optimized, or OutputColumn is used</exception>
    public ValueTask<long> BulkReplaceAsync(SqlConnection connection, IAsyncEnumerable<TEntity> entities, Action<SqlBulkReplaceOptions>? configure = null,
        int timeout = 30, SqlBulkCopyOptions sqlBulkCopyOptions = SqlBulkCopyOptions.Default, CancellationToken cancellationToken = default)
    {
        return BulkReplaceCoreAsync(connection, columnDefinitions => new AsyncDisguisedDataReader<TEntity>(columnDefinitions, entities, cancellationToken),
            configure, timeout, sqlBulkCopyOptions, cancellationToken);
    }

    private ValueTask<long> BulkReplaceCoreAsync(SqlConnection connection, Func<List<DisguisedColumnDefinition<TEntity>>, DbDataReader> createReader,
        Action<SqlBulkReplaceOptions>? configure, int timeout, SqlBulkCopyOptions sqlBulkCopyOptions, CancellationToken cancellationToken)
    {
        if (_outputColumns.Count > 0)
        {
            throw new NotSupportedException("OutputColumn can't be combined with BulkReplaceAsync.");
        }

        var columnMappings = _columnDefinitions.Select(x => (x.ColumnName, QuoteName(x.ColumnName))).ToList();

        return BulkReplace.ReplaceAsync(connection, GetTableName(), _tableNameParts, columnMappings, _configureBulkCopy, async (bulkCopy, ct) =>
        {
            var reader = createReader(_columnDefinitions);
            await using (reader.ConfigureAwait(false))
            {
                await bulkCopy.WriteToServerAsync(reader, ct).ConfigureAwait(false);
            }
        }, configure, timeout, sqlBulkCopyOptions, cancellationToken);
    }

    /// <summary>
    /// Quoted column names for the upsert, validated against the mapping
    /// </summary>
    private sealed record UpsertColumns(List<string> Keys, List<string> Updated, bool OnlyUpdateWhenChanged, DuplicateKeyHandling DuplicateKeyHandling);

    private UpsertColumns GetUpsertColumns(SqlBulkUpsertOptions options)
    {
        if (options.MatchOnColumns.Count == 0)
        {
            throw new InvalidOperationException("BulkUpsertAsync needs to know how to match the rows, call MatchOn with the key columns.");
        }

        string GetMappedColumnName(string columnName, string option) =>
            _columnDefinitions.FirstOrDefault(x => string.Equals(x.ColumnName, columnName, StringComparison.OrdinalIgnoreCase))?.ColumnName
            ?? throw new InvalidOperationException($"{option} column '{columnName}' is not mapped. Only mapped columns can be used in {option}.");

        var keys = options.MatchOnColumns.Select(x => GetMappedColumnName(x, nameof(SqlBulkUpsertOptions.MatchOn))).ToList();
        var ignored = options.IgnoreOnUpdateColumns.Select(x => GetMappedColumnName(x, nameof(SqlBulkUpsertOptions.IgnoreOnUpdate))).ToList();

        var updated = _columnDefinitions
            .Select(x => x.ColumnName)
            .Where(x => !keys.Contains(x) && !ignored.Contains(x))
            .ToList();

        return new UpsertColumns(keys.Select(QuoteName).ToList(), updated.Select(QuoteName).ToList(), options.UpdateOnlyWhenChanged, options.DuplicateKeyHandling);
    }

    private async ValueTask<long> WriteToServerAsync(SqlConnection connection, SqlTransaction? transaction, string destinationTableName, DbDataReader reader,
        List<(string Source, string Destination)> extraColumnMappings, int timeout, SqlBulkCopyOptions sqlBulkCopyOptions, CancellationToken cancellationToken)
    {
        await using (reader.ConfigureAwait(false))
        {
            using var bulkCopy = new SqlBulkCopy(connection, sqlBulkCopyOptions, transaction);
            bulkCopy.DestinationTableName = destinationTableName;
            bulkCopy.BulkCopyTimeout = timeout;

            foreach (var columnDefinition in _columnDefinitions)
            {
                bulkCopy.ColumnMappings.Add(columnDefinition.ColumnName, QuoteName(columnDefinition.ColumnName));
            }

            foreach (var (source, destination) in extraColumnMappings)
            {
                bulkCopy.ColumnMappings.Add(source, destination);
            }

            _configureBulkCopy?.Invoke(bulkCopy);

            await bulkCopy.WriteToServerAsync(reader, cancellationToken).ConfigureAwait(false);
            return bulkCopy.RowsCopied64;
        }
    }

    private const string RowNumberColumnName = "SqlBulkCopyHelper_RowNumber";
    private static readonly string RowNumberColumn = Quote(RowNumberColumnName);

    /// <summary>
    /// The temp tables used when inserting with output or upserting. Staging gets the entities, Output gets the values from OUTPUT.
    /// </summary>
    private readonly record struct StagingTables(string Staging, string Output)
    {
        public static StagingTables Create()
        {
            var suffix = Guid.NewGuid().ToString("N");
            return new StagingTables(Quote("#SqlBulkCopyHelper_Staging_" + suffix), Quote("#SqlBulkCopyHelper_Output_" + suffix));
        }
    }

    /// <summary>
    /// SqlBulkCopy can't return values generated by the database, so the entities are bulk copied to a staging table together with their row number.
    /// A MERGE that never matches then inserts them into the target table, since MERGE (unlike INSERT) can OUTPUT columns from the source.
    /// That gives the row number together with the generated values, which is used to set them on the right entity.
    /// </summary>
    private async ValueTask<long> InsertWithOutputAsync(SqlConnection connection, SqlTransaction? transaction,
        Func<List<DisguisedColumnDefinition<TEntity>>, Action<TEntity>?, DbDataReader> createReader,
        int timeout, SqlBulkCopyOptions sqlBulkCopyOptions, CancellationToken cancellationToken)
    {
        var outputColumns = _outputColumns.ToList();
        var outputColumnNames = outputColumns.Select(x => QuoteName(x.ColumnName)).ToList();
        var tables = StagingTables.Create();
        var targetTable = GetTableName();

        try
        {
            await CreateStagingTablesAsync(connection, transaction, tables, targetTable, outputColumnNames, timeout, cancellationToken).ConfigureAwait(false);

            var (_, entities) = await CopyToStagingAsync(connection, transaction, tables.Staging, createReader, keepEntities: true,
                timeout, sqlBulkCopyOptions, cancellationToken).ConfigureAwait(false);

            var sb = new StringBuilder();
            AppendInsert(sb, targetTable, tables.Staging, MappedColumnNames(), tables, outputColumnNames);
            AppendSelectOutput(sb, tables, outputColumnNames);

            return await ExecuteReaderAsync(connection, transaction, sb.ToString(), timeout,
                reader => ReadOutputAsync(reader, entities!, outputColumns, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await DropStagingTablesAsync(connection, transaction, tables, timeout).ConfigureAwait(false);
        }
    }

    private async ValueTask<SqlBulkUpsertResult> UpsertAsync(SqlConnection connection, SqlTransaction? transaction,
        Func<List<DisguisedColumnDefinition<TEntity>>, Action<TEntity>?, DbDataReader> createReader, UpsertColumns upsertColumns,
        int timeout, SqlBulkCopyOptions sqlBulkCopyOptions, CancellationToken cancellationToken)
    {
        var outputColumns = _outputColumns.ToList();
        var outputColumnNames = outputColumns.Select(x => QuoteName(x.ColumnName)).ToList();
        var tables = StagingTables.Create();
        var targetTable = GetTableName();

        try
        {
            await CreateStagingTablesAsync(connection, transaction, tables, targetTable, outputColumnNames, timeout, cancellationToken).ConfigureAwait(false);

            // The entities are only needed to set the output values on them
            var (stagedRows, entities) = await CopyToStagingAsync(connection, transaction, tables.Staging, createReader, keepEntities: outputColumns.Count > 0,
                timeout, sqlBulkCopyOptions, cancellationToken).ConfigureAwait(false);

            // An UPDATE where several source rows match the same row would use one of them at random, so duplicates are handled first
            var duplicatesRemoved = await HandleDuplicateKeysAsync(connection, transaction, tables, upsertColumns, timeout, cancellationToken).ConfigureAwait(false);

            var sql = CreateUpsertScript(targetTable, tables, upsertColumns, MappedColumnNames(), outputColumnNames);

            return await ExecuteReaderAsync(connection, transaction, sql, timeout, async reader =>
            {
                await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                var inserted = reader.GetInt64(0);
                var updated = reader.GetInt64(1);

                if (outputColumns.Count > 0)
                {
                    await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                    await ReadOutputAsync(reader, entities!, outputColumns, cancellationToken).ConfigureAwait(false);
                }

                return new SqlBulkUpsertResult(inserted, updated, stagedRows - duplicatesRemoved - inserted - updated, duplicatesRemoved);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await DropStagingTablesAsync(connection, transaction, tables, timeout).ConfigureAwait(false);
        }
    }

    private List<string> MappedColumnNames() => _columnDefinitions.Select(x => QuoteName(x.ColumnName)).ToList();

    /// <summary>
    /// The output table is only created if there are output columns
    /// </summary>
    private Task CreateStagingTablesAsync(SqlConnection connection, SqlTransaction? transaction, StagingTables tables, string targetTable,
        List<string> outputColumnNames, int timeout, CancellationToken cancellationToken)
    {
        var sql = CreateEmptyCopyScript(tables.Staging, targetTable, MappedColumnNames());
        if (outputColumnNames.Count > 0)
        {
            sql += CreateEmptyCopyScript(tables.Output, targetTable, outputColumnNames);
        }

        return ExecuteNonQueryAsync(connection, transaction, sql, timeout, cancellationToken);
    }

    /// <summary>
    /// Copies the entities to the staging table together with their row number, which starts at 0 and is the index in the source.
    /// </summary>
    /// <returns>Number of rows copied, and the entities in row number order if keepEntities is true</returns>
    private async ValueTask<(long Rows, List<TEntity>? Entities)> CopyToStagingAsync(SqlConnection connection, SqlTransaction? transaction, string stagingTable,
        Func<List<DisguisedColumnDefinition<TEntity>>, Action<TEntity>?, DbDataReader> createReader, bool keepEntities,
        int timeout, SqlBulkCopyOptions sqlBulkCopyOptions, CancellationToken cancellationToken)
    {
        var entities = keepEntities ? new List<TEntity>() : null;
        var rowNumber = -1L;

        var rowNumberColumnDefinition = new DisguisedColumnDefinition<TEntity>
        {
            ColumnName = RowNumberColumnName,
            Type = typeof(long),
            Nullable = false,
            PropertyGetter = _ => rowNumber
        };

        Action<TEntity> onRowRead = entities is null
            ? _ => rowNumber++
            : entity =>
            {
                rowNumber++;
                entities.Add(entity);
            };

        var rows = await WriteToServerAsync(connection, transaction, stagingTable, createReader([.. _columnDefinitions, rowNumberColumnDefinition], onRowRead),
            extraColumnMappings: [(RowNumberColumnName, RowNumberColumn)], timeout, sqlBulkCopyOptions, cancellationToken).ConfigureAwait(false);

        return (rows, entities);
    }

    /// <returns>Number of rows removed from the staging table</returns>
    private static async ValueTask<long> HandleDuplicateKeysAsync(SqlConnection connection, SqlTransaction? transaction, StagingTables tables,
        UpsertColumns upsertColumns, int timeout, CancellationToken cancellationToken)
    {
        var keyList = string.Join(", ", upsertColumns.Keys);

        if (upsertColumns.DuplicateKeyHandling == DuplicateKeyHandling.Throw)
        {
            var sql = $"SELECT TOP (5) {keyList}, MIN({RowNumberColumn}), MAX({RowNumberColumn}), COUNT_BIG(*) FROM {tables.Staging}{Environment.NewLine}" +
                      $"GROUP BY {keyList} HAVING COUNT_BIG(*) > 1 ORDER BY MIN({RowNumberColumn});";

            var duplicates = await ExecuteReaderAsync(connection, transaction, sql, timeout, async reader =>
            {
                var result = new List<string>();
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var keys = string.Join(", ", Enumerable.Range(0, upsertColumns.Keys.Count).Select(i => FormatValue(reader.GetValue(i))));
                    var count = upsertColumns.Keys.Count;
                    result.Add($"({keys}) {reader.GetInt64(count + 2)} times, first at index {reader.GetInt64(count)} and last at index {reader.GetInt64(count + 1)}");
                }

                return result;
            }, cancellationToken).ConfigureAwait(false);

            if (duplicates.Count > 0)
            {
                throw new InvalidOperationException(
                    $"The source has more than one row with the same key ({keyList}): {string.Join("; ", duplicates)}. " +
                    $"Use OnDuplicateKey(DuplicateKeyHandling.KeepFirst) or KeepLast to keep one of them.");
            }

            return 0;
        }

        var order = upsertColumns.DuplicateKeyHandling == DuplicateKeyHandling.KeepFirst ? "ASC" : "DESC";
        var deleteSql = $"WITH D AS (SELECT ROW_NUMBER() OVER (PARTITION BY {keyList} ORDER BY {RowNumberColumn} {order}) AS N FROM {tables.Staging}){Environment.NewLine}" +
                        $"DELETE FROM D WHERE N > 1;{Environment.NewLine}" +
                        "SELECT CAST(@@ROWCOUNT AS bigint);";

        return await ExecuteReaderAsync(connection, transaction, deleteSql, timeout, async reader =>
        {
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            return reader.GetInt64(0);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static string FormatValue(object value) => value switch
    {
        DBNull => "NULL",
        string s => "'" + s + "'",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };

    /// <summary>
    /// Returns the number of inserted and updated rows, and then the output values if there are output columns.
    /// </summary>
    private static string CreateUpsertScript(string targetTable, StagingTables tables, UpsertColumns upsertColumns, List<string> columns, List<string> outputColumnNames)
    {
        var keyMatch = string.Join(" AND ", upsertColumns.Keys.Select(x => $"T.{x} = S.{x}"));
        var sb = new StringBuilder();
        sb.AppendLine("DECLARE @Inserted bigint = 0, @Updated bigint = 0;");

        if (upsertColumns.Updated.Count > 0)
        {
            sb.Append("UPDATE T SET ").AppendJoin(", ", upsertColumns.Updated.Select(x => $"T.{x} = S.{x}")).AppendLine();
            AppendOutputInto(sb, tables, outputColumnNames);
            sb.Append("FROM ").Append(targetTable).AppendLine(" AS T WITH (UPDLOCK, HOLDLOCK)");
            sb.Append("JOIN ").Append(tables.Staging).Append(" AS S ON ").AppendLine(keyMatch);

            if (upsertColumns.OnlyUpdateWhenChanged)
            {
                // EXCEPT considers NULL equal to NULL, which <> doesn't
                sb.Append("WHERE EXISTS (SELECT ").AppendJoin(", ", upsertColumns.Updated.Select(x => "S." + x))
                    .Append(" EXCEPT SELECT ").AppendJoin(", ", upsertColumns.Updated.Select(x => "T." + x)).AppendLine(")");
            }

            sb.AppendLine(";");
            sb.AppendLine("SET @Updated = @@ROWCOUNT;");
        }

        // The rows that don't exist in the table. HOLDLOCK keeps the range locked, so no one else can insert the same key until the transaction is done.
        var newRows = $"(SELECT * FROM {tables.Staging} AS S WHERE NOT EXISTS (SELECT 1 FROM {targetTable} AS T WITH (UPDLOCK, HOLDLOCK) WHERE {keyMatch}))";
        AppendInsert(sb, targetTable, newRows, columns, tables, outputColumnNames);
        sb.AppendLine("SET @Inserted = @@ROWCOUNT;");

        // Rows that matched but were not updated should also get their output values
        if (outputColumnNames.Count > 0 && (upsertColumns.Updated.Count == 0 || upsertColumns.OnlyUpdateWhenChanged))
        {
            sb.Append("INSERT INTO ").Append(tables.Output).Append(" (").Append(RowNumberColumn).Append(string.Concat(outputColumnNames.Select(x => ", " + x))).AppendLine(")");
            sb.Append("SELECT S.").Append(RowNumberColumn).Append(string.Concat(outputColumnNames.Select(x => ", T." + x)))
                .Append(" FROM ").Append(tables.Staging).Append(" AS S JOIN ").Append(targetTable).Append(" AS T ON ").AppendLine(keyMatch);
            sb.Append("WHERE NOT EXISTS (SELECT 1 FROM ").Append(tables.Output).Append(" AS O WHERE O.").Append(RowNumberColumn).Append(" = S.").Append(RowNumberColumn).AppendLine(");");
        }

        sb.AppendLine("SELECT @Inserted, @Updated;");

        if (outputColumnNames.Count > 0)
        {
            AppendSelectOutput(sb, tables, outputColumnNames);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Reads the row number and the output columns, and sets the values on the entity with that row number.
    /// </summary>
    /// <returns>Number of rows read</returns>
    private static async ValueTask<long> ReadOutputAsync(SqlDataReader reader, List<TEntity> entities, List<OutputColumnDefinition<TEntity>> outputColumns,
        CancellationToken cancellationToken)
    {
        long rows = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var entity = entities[checked((int)reader.GetInt64(0))];
            for (var i = 0; i < outputColumns.Count; i++)
            {
                outputColumns[i].SetValue(entity, reader, i + 1);
            }

            rows++;
        }

        return rows;
    }

    private static async ValueTask DropStagingTablesAsync(SqlConnection connection, SqlTransaction? transaction, StagingTables tables, int timeout)
    {
        try
        {
            // The tables are gone if the connection is closed or the transaction is rolled back, but not if the caller keeps using the connection
            await ExecuteNonQueryAsync(connection, transaction,
                $"DROP TABLE IF EXISTS {tables.Staging};{Environment.NewLine}DROP TABLE IF EXISTS {tables.Output};",
                timeout, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // A failed clean-up should not hide the original exception, for example if the transaction is doomed
        }
    }

    /// <summary>
    /// Creates an empty table with the row number column and the same column types as the target table. The UNION ALL makes sure an IDENTITY is not copied.
    /// </summary>
    private static string CreateEmptyCopyScript(string tableName, string targetTable, List<string> columns)
    {
        var columnList = string.Concat(columns.Select(x => ", " + x));
        return $"SELECT TOP (0) CAST(0 AS bigint) AS {RowNumberColumn}{columnList} INTO {tableName} FROM {targetTable}{Environment.NewLine}" +
               $"UNION ALL SELECT TOP (0) CAST(0 AS bigint){columnList} FROM {targetTable};{Environment.NewLine}";
    }

    /// <summary>
    /// A MERGE that never matches, so all rows in source are inserted. The source must have the alias S.
    /// </summary>
    private static void AppendInsert(StringBuilder sb, string targetTable, string source, List<string> columns, StagingTables tables, List<string> outputColumnNames)
    {
        sb.Append("MERGE INTO ").Append(targetTable).AppendLine(" AS T");
        sb.Append("USING ").Append(source).AppendLine(" AS S ON 1 = 0");
        sb.Append("WHEN NOT MATCHED THEN INSERT ");

        if (columns.Count == 0)
        {
            sb.AppendLine("DEFAULT VALUES");
        }
        else
        {
            sb.Append('(').AppendJoin(", ", columns).Append(") VALUES (").AppendJoin(", ", columns.Select(x => "S." + x)).AppendLine(")");
        }

        AppendOutputInto(sb, tables, outputColumnNames);
        sb.AppendLine(";");
    }

    /// <summary>
    /// OUTPUT INTO, since OUTPUT directly to the client is not allowed if the target table has triggers. Nothing is added without output columns.
    /// </summary>
    private static void AppendOutputInto(StringBuilder sb, StagingTables tables, List<string> outputColumnNames)
    {
        if (outputColumnNames.Count == 0)
        {
            return;
        }

        sb.Append("OUTPUT S.").Append(RowNumberColumn).Append(string.Concat(outputColumnNames.Select(x => ", inserted." + x)))
            .Append(" INTO ").Append(tables.Output).Append(" (").Append(RowNumberColumn).Append(string.Concat(outputColumnNames.Select(x => ", " + x))).AppendLine(")");
    }

    private static void AppendSelectOutput(StringBuilder sb, StagingTables tables, List<string> outputColumnNames)
    {
        sb.Append("SELECT ").Append(RowNumberColumn).Append(string.Concat(outputColumnNames.Select(x => ", " + x)))
            .Append(" FROM ").Append(tables.Output).Append(" ORDER BY ").Append(RowNumberColumn).AppendLine(";");
    }

    /// <summary>
    /// Can be used to add or change how Types are mapped for the value in SqlBulkCopyHelperColumnInfo.SchemaDefinition.
    /// Intention is just to use these mappings for a staging table. They might be on the "bigger" side.
    /// Nullable types (e.g. int?) use the mapping of their underlying type (int), and enums the mapping of their underlying type.
    /// </summary>
    public Dictionary<Type, string> SchemaDefinitionMapping { get; set; } = new()
    {
        { typeof(bool), "bit" },
        { typeof(byte), "tinyint" },
        { typeof(sbyte), "smallint" },
        { typeof(short), "smallint" },
        { typeof(ushort), "int" },
        { typeof(int), "int" },
        { typeof(uint), "bigint" },
        { typeof(long), "bigint" },
        { typeof(ulong), "numeric(20,0)" },
        { typeof(float), "real" },
        { typeof(double), "float" },
        { typeof(decimal), "numeric(38,15)" },
        { typeof(DateTime), "datetime2(7)" },
        { typeof(DateTimeOffset), "datetimeoffset(7)" },
        { typeof(DateOnly), "date" },
        { typeof(TimeOnly), "time(7)" },
        { typeof(TimeSpan), "time(7)" },
        { typeof(Guid), "uniqueidentifier" },
        { typeof(string), "nvarchar(max)" },
        { typeof(char), "nchar(1)" },
        { typeof(char[]), "nvarchar(max)" },
        { typeof(byte[]), "varbinary(max)" },
    };

    /// <summary>
    /// Get information about the columns that currently exists in this helper.
    /// </summary>
    /// <param name="columnsAreAlwaysNullable">If true all the SchemaDefinition will be nullable. If false it will be based on if the Type that was provided during mapping is nullable or not.</param>
    /// <returns>SqlBulkCopyHelperColumnInfo</returns>
    /// <exception cref="InvalidOperationException">If a mapped type has no mapping in SchemaDefinitionMapping</exception>
    public IEnumerable<SqlBulkCopyHelperColumnInfo> GetColumnInfo(bool columnsAreAlwaysNullable = true)
    {
        var sb = new StringBuilder();
        foreach (var columnDefinition in _columnDefinitions)
        {
            sb.Clear();
            sb.Append(QuoteName(columnDefinition.ColumnName));

            var lookupType = columnDefinition.Type.IsEnum
                ? Enum.GetUnderlyingType(columnDefinition.Type)
                : columnDefinition.Type;

            if (!SchemaDefinitionMapping.TryGetValue(lookupType, out var columnType))
            {
                throw new InvalidOperationException(
                    $"Column '{columnDefinition.ColumnName}' has type '{lookupType}' that has no database type in SchemaDefinitionMapping. " +
                    $"Add it, for example: helper.SchemaDefinitionMapping[typeof({lookupType.Name})] = \"nvarchar(max)\";");
            }

            sb.Append(' ').Append(columnType);

            if (!columnsAreAlwaysNullable && !columnDefinition.Nullable)
            {
                sb.Append(" not");
            }

            sb.Append(" null");

            yield return new SqlBulkCopyHelperColumnInfo(
                columnDefinition.ColumnName,
                QuoteName(columnDefinition.ColumnName),
                columnType,
                columnDefinition.Nullable,
                sb.ToString());
        }
    }

    /// <summary>
    /// This is more for creating a staging table.
    /// </summary>
    /// <param name="columnsAreAlwaysNullable">If true all columns will be nullable. If false it will be based on if the Type that was provided during mapping is nullable or not.</param>
    /// <param name="checkIfTableExists">If true, adds an if-statement around the "create table"-statement, to check if it already exists or not</param>
    /// <returns>A script that can be run against the database to create a staging table.</returns>
    /// <exception cref="InvalidOperationException">If a mapped type has no mapping in SchemaDefinitionMapping</exception>
    public string CreateTableScript(bool columnsAreAlwaysNullable = true, bool checkIfTableExists = false)
    {
        var sb = new StringBuilder();
        var schemaTableName = GetTableName();

        if (checkIfTableExists)
        {
            var objectName = _tableNameParts.Count == 1 && Unquote(_tableNameParts[0]).StartsWith('#')
                ? "tempdb.." + schemaTableName
                : schemaTableName;

            // Escape ' since the name is used in a string literal
            sb.Append("IF OBJECT_ID('").Append(objectName.Replace("'", "''")).AppendLine("') IS NULL");
            sb.AppendLine("BEGIN");
        }

        sb.Append("CREATE TABLE ").AppendLine(schemaTableName);
        sb.AppendLine("(");

        var columns = GetColumnInfo(columnsAreAlwaysNullable).Select(x => x.SchemaDefinition).ToList();

        for (var i = 0; i < columns.Count; i++)
        {
            sb.Append('\t').Append(columns[i]);

            if (i < columns.Count - 1)
            {
                sb.Append(',');
            }

            sb.AppendLine();
        }

        sb.AppendLine(");");

        if (checkIfTableExists)
        {
            sb.AppendLine("END");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Adds a mapping rule where we can infer the type from the lambda.
    /// </summary>
    /// <param name="columnName">Database column name</param>
    /// <param name="propertyGetter">Lambda for getting the value</param>
    /// <typeparam name="TProperty">The Type</typeparam>
    /// <returns>The SqlBulkCopyHelper so you can continue with the builder pattern</returns>
    public SqlBulkCopyHelper<TEntity> Map<TProperty>(string columnName, Func<TEntity, TProperty> propertyGetter)
    {
        return AddOrUpdateColumn(columnName, typeof(TProperty), entity => (object)propertyGetter(entity)!);
    }

    /// <summary>
    /// Adds a mapping rule where we can't infer the type from the lambda.
    /// </summary>
    /// <param name="columnName">Database column name</param>
    /// <param name="propertyGetter">Lambda for getting the value</param>
    /// <param name="propertyType">The Type</param>
    /// <returns>The SqlBulkCopyHelper so you can continue with the builder pattern</returns>
    public SqlBulkCopyHelper<TEntity> Map(string columnName, Func<TEntity, object> propertyGetter, Type propertyType)
    {
        return AddOrUpdateColumn(columnName, propertyType, entity => propertyGetter(entity)!);
    }

    internal SqlBulkCopyHelper<TEntity> Map(string columnName, Func<TEntity, object> propertyGetter, Type propertyType, bool nullable)
    {
        return AddOrUpdateColumn(columnName, propertyType, propertyGetter, nullable);
    }

    /// <summary>
    /// Mapping is basically a dictionary where the columnName is the key.
    /// Column names are case-insensitive, like in SQL Server, so "Id" and "id" is the same column.
    /// If you for some reason need to remove a mapping, use this method.
    /// </summary>
    /// <param name="columnName">Removes the mapping if it exits. If it does not exist, nothing happens.</param>
    /// <returns>The SqlBulkCopyHelper so you can continue with the builder pattern</returns>
    public SqlBulkCopyHelper<TEntity> RemoveMap(string columnName)
    {
        var columnDefinition = _columnDefinitions.FirstOrDefault(x => string.Equals(x.ColumnName, columnName, StringComparison.OrdinalIgnoreCase));
        if (columnDefinition is not null)
        {
            _columnDefinitions.Remove(columnDefinition);
        }

        return this;
    }

    // nullable: If null, reference types and Nullable<T> are considered nullable
    private SqlBulkCopyHelper<TEntity> AddOrUpdateColumn(string columnName, Type type, Func<TEntity, object> propertyGetter, bool? nullable = null)
    {
        RemoveMap(columnName);

        var underlyingType = Nullable.GetUnderlyingType(type);

        _columnDefinitions.Add(new DisguisedColumnDefinition<TEntity>
        {
            ColumnName = columnName,
            Type = underlyingType ?? type,
            Nullable = nullable ?? (underlyingType is not null || !type.IsValueType),
            PropertyGetter = propertyGetter
        });

        return this;
    }

    /// <summary>
    /// Wraps table and column names in brackets []
    /// </summary>
    public SqlBulkCopyHelper<TEntity> UseBracketQuoting()
    {
        _useQuoting = true;
        return this;
    }

    /// <summary>
    /// Configure the SqlBulkCopy instance used by BulkInsertAsync, for example BatchSize, NotifyAfter or SqlRowsCopied.
    /// It's called after this helper has applied its own settings, so it's possible to override them (for example BulkCopyTimeout).
    /// Calling this method again replaces the previous configuration.
    /// </summary>
    /// <param name="configure">Action that will be called with the SqlBulkCopy instance before the insert starts</param>
    /// <returns>The SqlBulkCopyHelper so you can continue with the builder pattern</returns>
    public SqlBulkCopyHelper<TEntity> ConfigureBulkCopy(Action<SqlBulkCopy> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _configureBulkCopy = configure;
        return this;
    }

    /// <summary>
    /// Reads back a value that the database generated for each inserted row, like an IDENTITY, a DEFAULT or a computed column, and passes it to the setter together with its entity.
    /// Column names are case-insensitive, calling it again for the same column replaces the previous setter.
    /// <para>
    /// When at least one output column is used, BulkInsertAsync bulk copies to a staging temp table and inserts into the table with a MERGE.
    /// The entities are kept in memory until the insert is done, so the values can be set on them.
    /// The insert behaves like an ordinary INSERT: triggers fire, constraints are checked and NULL is inserted as NULL, instead of the column's DEFAULT.
    /// SqlBulkCopyOptions.KeepIdentity is not supported.
    /// </para>
    /// </summary>
    /// <param name="columnName">Column in the table to read the value from. It doesn't have to be mapped.</param>
    /// <param name="setter">Called with the entity and the value that was generated for it</param>
    /// <typeparam name="TValue">The type to read the value as</typeparam>
    /// <returns>The SqlBulkCopyHelper so you can continue with the builder pattern</returns>
    public SqlBulkCopyHelper<TEntity> OutputColumn<TValue>(string columnName, Action<TEntity, TValue> setter)
    {
        if (string.IsNullOrWhiteSpace(columnName)) throw new ArgumentNullException(nameof(columnName));
        ArgumentNullException.ThrowIfNull(setter);

        _outputColumns.RemoveAll(x => string.Equals(x.ColumnName, columnName, StringComparison.OrdinalIgnoreCase));
        _outputColumns.Add(OutputColumnDefinition<TEntity>.Create(columnName, setter));
        return this;
    }

    /// <summary>
    /// Reads back a value that the database generated for each inserted row and sets it on the property, for example .OutputColumn(x => x.Id).
    /// See the other OutputColumn overload for how the insert is done.
    /// </summary>
    /// <param name="property">The property or field to set, like x => x.Id</param>
    /// <param name="columnName">Column in the table to read the value from. Default the name of the property.</param>
    /// <typeparam name="TValue">The type of the property</typeparam>
    /// <returns>The SqlBulkCopyHelper so you can continue with the builder pattern</returns>
    /// <exception cref="ArgumentException">If the expression is not a writable property or field directly on the entity</exception>
    /// <exception cref="InvalidOperationException">If the entity is a value type, since the value would be set on a copy</exception>
    public SqlBulkCopyHelper<TEntity> OutputColumn<TValue>(Expression<Func<TEntity, TValue>> property, string? columnName = null)
    {
        ArgumentNullException.ThrowIfNull(property);

        if (typeof(TEntity).IsValueType)
        {
            throw new InvalidOperationException($"{typeof(TEntity).Name} is a value type, so a property would be set on a copy. Use OutputColumn(columnName, setter) instead.");
        }

        if (property.Body is not MemberExpression { Member: PropertyInfo { CanWrite: true } or FieldInfo { IsInitOnly: false } } member
            || member.Expression != property.Parameters[0])
        {
            throw new ArgumentException("The expression must be a writable property or field directly on the entity, like x => x.Id", nameof(property));
        }

        var value = Expression.Parameter(typeof(TValue), "value");
        var setter = Expression.Lambda<Action<TEntity, TValue>>(Expression.Assign(member, value), property.Parameters[0], value).Compile();

        return OutputColumn(string.IsNullOrWhiteSpace(columnName) ? member.Member.Name : columnName, setter);
    }

    private readonly List<OutputColumnDefinition<TEntity>> _outputColumns = [];
    private Action<SqlBulkCopy>? _configureBulkCopy;
    private bool _useQuoting;

    private string QuoteName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Can't quote a null or empty name", nameof(name));

        return _useQuoting ? Quote(name) : name;
    }

    /// <summary>
    /// With quoting, each part is quoted. Parts that already are quoted are kept as they are.
    /// Without quoting, the table name is used as it was provided.
    /// Empty parts (like in "db..Table") are kept empty.
    /// </summary>
    private string GetTableName() => _useQuoting ? QuoteMultipartName(_tableNameParts) : _tableName;
}
