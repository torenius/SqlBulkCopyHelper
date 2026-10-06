using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using static SqlBulkCopyHelper.SqlExecution;
using static SqlBulkCopyHelper.SqlNames;

namespace SqlBulkCopyHelper;

/// <summary>
/// Replaces all rows in a table, see SqlBulkCopyHelper.BulkReplaceAsync.
/// Shared by SqlBulkCopyHelper and the SqlConnection extensions, which only differ in the column mappings and how the rows are written.
/// </summary>
internal static class BulkReplace
{
    /// <param name="connection">Opened and closed again if it's closed</param>
    /// <param name="tableName">The table name to read the table structure with, quoted or as the caller provided it</param>
    /// <param name="tableNameParts">The parts of the table name, to check that it's not in another database or a temp table</param>
    /// <param name="columnMappings">Source column and destination column, added to the SqlBulkCopy before configureBulkCopy</param>
    /// <param name="configureBulkCopy">Can change the SqlBulkCopy, also the column mappings</param>
    /// <param name="writeRows">Writes the rows with the SqlBulkCopy to the incoming table</param>
    /// <param name="configure">Configures the swap</param>
    /// <param name="timeout">Number of seconds for each step</param>
    /// <param name="sqlBulkCopyOptions">TableLock is always added</param>
    /// <param name="cancellationToken">Cancels the operation</param>
    public static async ValueTask<long> ReplaceAsync(SqlConnection connection, string tableName, List<string> tableNameParts,
        List<(string Source, string Destination)> columnMappings, Action<SqlBulkCopy>? configureBulkCopy, Func<SqlBulkCopy, CancellationToken, Task> writeRows,
        Action<SqlBulkReplaceOptions>? configure, int timeout, SqlBulkCopyOptions sqlBulkCopyOptions, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (tableNameParts.Count > 2)
        {
            throw new NotSupportedException($"BulkReplaceAsync only supports tables in the current database, so '{tableName}' can't have a database part.");
        }

        if (Unquote(tableNameParts[^1]).StartsWith('#'))
        {
            throw new NotSupportedException("BulkReplaceAsync doesn't support temp tables, since no one else can read them. Use TRUNCATE TABLE and BulkInsertAsync instead.");
        }

        var options = new SqlBulkReplaceOptions();
        configure?.Invoke(options);

        return await ExecuteAsync(connection, sqlTransaction: null, useOwnTransaction: false,
            _ => CopyAndSwapAsync(connection, tableName, columnMappings, configureBulkCopy, writeRows, options, timeout, sqlBulkCopyOptions, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<long> CopyAndSwapAsync(SqlConnection connection, string tableName, List<(string Source, string Destination)> columnMappings,
        Action<SqlBulkCopy>? configureBulkCopy, Func<SqlBulkCopy, CancellationToken, Task> writeRows, SqlBulkReplaceOptions options,
        int timeout, SqlBulkCopyOptions sqlBulkCopyOptions, CancellationToken cancellationToken)
    {
        var table = await TableStructure.ReadAsync(connection, tableName, timeout, cancellationToken).ConfigureAwait(false);
        var (incoming, outgoing) = CreateSwapTableNames(table);

        // Only the tables created here are dropped, a table with the same name could belong to another replace
        var dropTables = new List<string>();
        try
        {
            await ExecuteNonQueryAsync(connection, null, table.CreateTableScript(incoming), timeout, cancellationToken).ConfigureAwait(false);
            dropTables.Add(incoming);
            await ExecuteNonQueryAsync(connection, null, table.CreateTableScript(outgoing), timeout, cancellationToken).ConfigureAwait(false);
            dropTables.Add(outgoing);

            long rows;
            // No one else uses the incoming table, so it's locked for a faster load
            using (var bulkCopy = new SqlBulkCopy(connection, sqlBulkCopyOptions | SqlBulkCopyOptions.TableLock, null))
            {
                bulkCopy.DestinationTableName = incoming;
                bulkCopy.BulkCopyTimeout = timeout;

                foreach (var (source, destination) in columnMappings)
                {
                    bulkCopy.ColumnMappings.Add(source, destination);
                }

                configureBulkCopy?.Invoke(bulkCopy);
                ThrowIfMappedColumnIsMissing(bulkCopy, table);

                await writeRows(bulkCopy, cancellationToken).ConfigureAwait(false);
                rows = bulkCopy.RowsCopied64;
            }

            var indexesAndConstraints = table.CreateIndexesAndConstraintsScript(incoming);
            if (indexesAndConstraints.Length > 0)
            {
                await ExecuteNonQueryAsync(connection, null, indexesAndConstraints, timeout, cancellationToken).ConfigureAwait(false);
            }

            await SwapAsync(connection, table, incoming, outgoing, options, timeout, cancellationToken).ConfigureAwait(false);
            return rows;
        }
        finally
        {
            // After the swap the outgoing table has the old rows, and if the swap didn't happen the incoming table has the new rows
            if (dropTables.Count > 0)
            {
                try
                {
                    await ExecuteNonQueryAsync(connection, null, string.Concat(dropTables.Select(x => $"DROP TABLE IF EXISTS {x};{Environment.NewLine}")),
                        timeout, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // A failed clean-up should not hide the original exception
                }
            }
        }
    }

    /// <summary>
    /// SqlBulkCopy's own error doesn't tell which column it is. Mappings by ordinal are left to SqlBulkCopy.
    /// </summary>
    private static void ThrowIfMappedColumnIsMissing(SqlBulkCopy bulkCopy, TableStructure table)
    {
        foreach (SqlBulkCopyColumnMapping mapping in bulkCopy.ColumnMappings)
        {
            if (string.IsNullOrEmpty(mapping.DestinationColumn))
            {
                continue;
            }

            var columnName = Unquote(mapping.DestinationColumn);
            if (!table.ColumnNames.Contains(columnName, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"The column {Quote(columnName)} is mapped, but doesn't exist in {table.QuotedName}. Columns are mapped by name.");
            }
        }
    }

    /// <summary>
    /// The old rows are switched out to the empty outgoing table and then the new rows are switched in, since SWITCH can only switch into an empty table.
    /// Switching out, instead of TRUNCATE, makes it possible to wait at low priority, and the old rows are dropped after the transaction.
    /// </summary>
    private static async Task SwapAsync(SqlConnection connection, TableStructure table, string incoming, string outgoing, SqlBulkReplaceOptions options,
        int timeout, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        sb.Append("ALTER TABLE ").Append(table.QuotedName).Append(" SWITCH TO ").Append(outgoing);

        var swapTimeout = timeout;
        if (options.LowPriorityMaxDurationMinutes is { } minutes)
        {
            sb.Append(" WITH (WAIT_AT_LOW_PRIORITY (MAX_DURATION = ").Append(minutes.ToString(CultureInfo.InvariantCulture))
                .Append(" MINUTES, ABORT_AFTER_WAIT = ").Append(options.AbortAfterWait.ToString().ToUpperInvariant()).Append("))");

            // The wait is part of the command, so it shouldn't time out before the wait is over
            swapTimeout = timeout == 0 ? 0 : timeout + minutes * 60;
        }

        sb.AppendLine(";");

        // The table is already locked by the first SWITCH, so this doesn't wait
        sb.Append("ALTER TABLE ").Append(incoming).Append(" SWITCH TO ").Append(table.QuotedName).AppendLine(";");

        if (table.HasIdentity)
        {
            // SWITCH doesn't update the identity of the table, so the next insert could get a value that one of the new rows already has
            sb.Append("DBCC CHECKIDENT (N'").Append(table.QuotedName.Replace("'", "''")).AppendLine("', RESEED) WITH NO_INFOMSGS;");
        }

        await ExecuteAsync(connection, sqlTransaction: null, useOwnTransaction: true, async transaction =>
        {
            await ExecuteNonQueryAsync(connection, transaction, sb.ToString(), swapTimeout, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The incoming and outgoing tables, in the same schema as the table since SWITCH requires it. A long table name is shortened, since a name can be at most 128 characters.
    /// </summary>
    private static (string Incoming, string Outgoing) CreateSwapTableNames(TableStructure table)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

        string CreateName(string kind)
        {
            var suffix = "_" + kind + "_" + timestamp;
            var name = table.TableName.Length + suffix.Length > 128 ? table.TableName[..(128 - suffix.Length)] : table.TableName;
            return Quote(table.SchemaName) + "." + Quote(name + suffix);
        }

        return (CreateName("Incoming"), CreateName("Outgoing"));
    }
}
