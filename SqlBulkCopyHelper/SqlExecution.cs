using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace SqlBulkCopyHelper;

/// <summary>
/// Runs commands and handles the connection and transaction around them
/// </summary>
internal static class SqlExecution
{
    /// <summary>
    /// Opens the connection if it's closed, and closes it again when done.
    /// With useOwnTransaction a transaction is started, committed if work succeeds and rolled back if it fails.
    /// </summary>
    public static async ValueTask<T> ExecuteAsync<T>(SqlConnection connection, SqlTransaction? sqlTransaction, bool useOwnTransaction,
        Func<SqlTransaction?, ValueTask<T>> work, CancellationToken cancellationToken)
    {
        var closeConnection = false;
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            closeConnection = true;
        }

        SqlTransaction? ownTransaction = null;
        try
        {
            if (useOwnTransaction)
            {
                ownTransaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                var result = await work(sqlTransaction ?? ownTransaction).ConfigureAwait(false);

                if (ownTransaction is not null)
                {
                    await ownTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }

                return result;
            }
            catch when (ownTransaction is not null)
            {
                try
                {
                    await ownTransaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // A failed rollback should not hide the original exception
                }

                throw;
            }
        }
        finally
        {
            if (ownTransaction is not null)
            {
                await ownTransaction.DisposeAsync().ConfigureAwait(false);
            }

            if (closeConnection)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    public static async Task ExecuteNonQueryAsync(SqlConnection connection, SqlTransaction? transaction, string sql, int timeout, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = transaction;
            command.CommandTimeout = timeout;
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public static async ValueTask<T> ExecuteReaderAsync<T>(SqlConnection connection, SqlTransaction? transaction, string sql, int timeout,
        Func<SqlDataReader, ValueTask<T>> read, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = transaction;
            command.CommandTimeout = timeout;
            command.CommandText = sql;

            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                return await read(reader).ConfigureAwait(false);
            }
        }
    }
}
