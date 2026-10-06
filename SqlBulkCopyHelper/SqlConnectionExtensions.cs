using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using static SqlBulkCopyHelper.SqlNames;

namespace SqlBulkCopyHelper;

/// <summary>
/// Extension methods for bulk inserting and replacing directly on a SqlConnection.
/// </summary>
public static class SqlConnectionExtensions
{
    /// <param name="connection">Connection to execute the insert on</param>
    extension(SqlConnection connection)
    {
        /// <summary>
        /// Will try to bulk insert all the entities using SqlBulkCopy.
        /// Mapping will be done by selecting all public properties and then assuming that their names match the database column names.
        /// </summary>
        /// <param name="tableName">Table to insert data into</param>
        /// <param name="entities">Entities to insert</param>
        /// <param name="columnNameFunc">How to map from PropertyInfo to the desired column name. Default just PropertyInfo.Name</param>
        /// <param name="createTableIfNotExists">If true it will first make a call to creating the table that "CreateTableScript" generates. See SqlBulkCopyHelper.BulkInsertAsync for transaction behavior.</param>
        /// <param name="timeout">Number of seconds for the operation to complete before it times out. 0 equals no timeout. Default 30 seconds</param>
        /// <param name="sqlBulkCopyOptions">Different options that SqlBulkCopy will consider</param>
        /// <param name="sqlTransaction">If this should be done in a specific transaction or not. The caller is responsible for commit or rollback.</param>
        /// <param name="configureBulkCopy">Configure the SqlBulkCopy instance, for example BatchSize, NotifyAfter or SqlRowsCopied. See SqlBulkCopyHelper.ConfigureBulkCopy</param>
        /// <param name="cancellationToken">Do you like to have the option to cancel the operation?</param>
        /// <returns>Number of rows inserted</returns>
        // Preferred over the IAsyncEnumerable overload for types that implement both, like an EF Core DbSet
        [OverloadResolutionPriority(1)]
        public ValueTask<long> BulkInsertAsync<T>(string tableName, IEnumerable<T> entities, Func<PropertyInfo, string>? columnNameFunc = null, bool createTableIfNotExists = false,
            int timeout = 30, SqlBulkCopyOptions sqlBulkCopyOptions = SqlBulkCopyOptions.Default, SqlTransaction? sqlTransaction = null,
            Action<SqlBulkCopy>? configureBulkCopy = null, CancellationToken cancellationToken = default) where T : class
        {
            return CreateEntityHelper<T>(tableName, columnNameFunc, configureBulkCopy)
                .BulkInsertAsync(connection, entities, createTableIfNotExists, timeout, sqlBulkCopyOptions, sqlTransaction, cancellationToken);
        }

        /// <summary>
        /// Will try to bulk insert all the entities using SqlBulkCopy, streamed from an IAsyncEnumerable without blocking threads.
        /// Mapping will be done by selecting all public properties and then assuming that their names match the database column names.
        /// A type that implements both IEnumerable and IAsyncEnumerable (like an EF Core DbSet) uses the IEnumerable overload, call .AsAsyncEnumerable() to use this one.
        /// </summary>
        /// <param name="tableName">Table to insert data into</param>
        /// <param name="entities">Entities to insert</param>
        /// <param name="columnNameFunc">How to map from PropertyInfo to the desired column name. Default just PropertyInfo.Name</param>
        /// <param name="createTableIfNotExists">If true it will first make a call to creating the table that "CreateTableScript" generates. See SqlBulkCopyHelper.BulkInsertAsync for transaction behavior.</param>
        /// <param name="timeout">Number of seconds for the operation to complete before it times out. 0 equals no timeout. Default 30 seconds</param>
        /// <param name="sqlBulkCopyOptions">Different options that SqlBulkCopy will consider</param>
        /// <param name="sqlTransaction">If this should be done in a specific transaction or not. The caller is responsible for commit or rollback.</param>
        /// <param name="configureBulkCopy">Configure the SqlBulkCopy instance, for example BatchSize, NotifyAfter or SqlRowsCopied. See SqlBulkCopyHelper.ConfigureBulkCopy</param>
        /// <param name="cancellationToken">Cancels the operation. It's also passed to the source when it's enumerated.</param>
        /// <returns>Number of rows inserted</returns>
        public ValueTask<long> BulkInsertAsync<T>(string tableName, IAsyncEnumerable<T> entities, Func<PropertyInfo, string>? columnNameFunc = null, bool createTableIfNotExists = false,
            int timeout = 30, SqlBulkCopyOptions sqlBulkCopyOptions = SqlBulkCopyOptions.Default, SqlTransaction? sqlTransaction = null,
            Action<SqlBulkCopy>? configureBulkCopy = null, CancellationToken cancellationToken = default) where T : class
        {
            return CreateEntityHelper<T>(tableName, columnNameFunc, configureBulkCopy)
                .BulkInsertAsync(connection, entities, createTableIfNotExists, timeout, sqlBulkCopyOptions, sqlTransaction, cancellationToken);
        }

        /// <summary>
        /// Will try to bulk insert all values using SqlBulkCopy.
        /// Meant for lists of simple values like int, int?, string, Guid or byte[].
        /// </summary>
        /// <param name="tableName">Table to insert data into</param>
        /// <param name="columnName">Name of the column to insert the values to</param>
        /// <param name="values">Values to insert</param>
        /// <param name="createTableIfNotExists">If true it will first make a call to creating the table that "CreateTableScript" generates. See SqlBulkCopyHelper.BulkInsertAsync for transaction behavior.</param>
        /// <param name="timeout">Number of seconds for the operation to complete before it times out. 0 equals no timeout. Default 30 seconds</param>
        /// <param name="sqlBulkCopyOptions">Different options that SqlBulkCopy will consider</param>
        /// <param name="sqlTransaction">If this should be done in a specific transaction or not. The caller is responsible for commit or rollback.</param>
        /// <param name="configureBulkCopy">Configure the SqlBulkCopy instance, for example BatchSize, NotifyAfter or SqlRowsCopied. See SqlBulkCopyHelper.ConfigureBulkCopy</param>
        /// <param name="cancellationToken">Do you like to have the option to cancel the operation?</param>
        /// <returns>Number of rows inserted</returns>
        // Preferred over the IAsyncEnumerable overload for types that implement both, like an EF Core DbSet
        [OverloadResolutionPriority(1)]
        public ValueTask<long> BulkInsertAsync<T>(string tableName, string columnName, IEnumerable<T> values, bool createTableIfNotExists = false,
            int timeout = 30, SqlBulkCopyOptions sqlBulkCopyOptions = SqlBulkCopyOptions.Default, SqlTransaction? sqlTransaction = null,
            Action<SqlBulkCopy>? configureBulkCopy = null, CancellationToken cancellationToken = default)
        {
            return CreateValueHelper<T>(tableName, columnName, configureBulkCopy)
                .BulkInsertAsync(connection, values, createTableIfNotExists, timeout, sqlBulkCopyOptions, sqlTransaction, cancellationToken);
        }

        /// <summary>
        /// Will try to bulk insert all values using SqlBulkCopy, streamed from an IAsyncEnumerable without blocking threads.
        /// Meant for lists of simple values like int, int?, string, Guid or byte[].
        /// </summary>
        /// <param name="tableName">Table to insert data into</param>
        /// <param name="columnName">Name of the column to insert the values to</param>
        /// <param name="values">Values to insert</param>
        /// <param name="createTableIfNotExists">If true it will first make a call to creating the table that "CreateTableScript" generates. See SqlBulkCopyHelper.BulkInsertAsync for transaction behavior.</param>
        /// <param name="timeout">Number of seconds for the operation to complete before it times out. 0 equals no timeout. Default 30 seconds</param>
        /// <param name="sqlBulkCopyOptions">Different options that SqlBulkCopy will consider</param>
        /// <param name="sqlTransaction">If this should be done in a specific transaction or not. The caller is responsible for commit or rollback.</param>
        /// <param name="configureBulkCopy">Configure the SqlBulkCopy instance, for example BatchSize, NotifyAfter or SqlRowsCopied. See SqlBulkCopyHelper.ConfigureBulkCopy</param>
        /// <param name="cancellationToken">Cancels the operation. It's also passed to the source when it's enumerated.</param>
        /// <returns>Number of rows inserted</returns>
        public ValueTask<long> BulkInsertAsync<T>(string tableName, string columnName, IAsyncEnumerable<T> values, bool createTableIfNotExists = false,
            int timeout = 30, SqlBulkCopyOptions sqlBulkCopyOptions = SqlBulkCopyOptions.Default, SqlTransaction? sqlTransaction = null,
            Action<SqlBulkCopy>? configureBulkCopy = null, CancellationToken cancellationToken = default)
        {
            return CreateValueHelper<T>(tableName, columnName, configureBulkCopy)
                .BulkInsertAsync(connection, values, createTableIfNotExists, timeout, sqlBulkCopyOptions, sqlTransaction, cancellationToken);
        }

        /// <summary>
        /// Replaces all rows in the table with the entities, without readers ever seeing the table empty or half-filled. See SqlBulkCopyHelper.BulkReplaceAsync for how it's done.
        /// Mapping will be done by selecting all public properties and then assuming that their names match the database column names.
        /// </summary>
        /// <param name="tableName">Table to replace the rows in</param>
        /// <param name="entities">All the rows the table should have</param>
        /// <param name="columnNameFunc">How to map from PropertyInfo to the desired column name. Default just PropertyInfo.Name</param>
        /// <param name="configure">Configures the swap, for example WaitAtLowPriority</param>
        /// <param name="timeout">Number of seconds for each step to complete before it times out, like the bulk copy, creating the indexes and the swap. 0 equals no timeout. Default 30 seconds</param>
        /// <param name="sqlBulkCopyOptions">Options for the SqlBulkCopy to the incoming table. TableLock is always used, since no one else uses that table.</param>
        /// <param name="configureBulkCopy">Configure the SqlBulkCopy instance, for example BatchSize, NotifyAfter or SqlRowsCopied. See SqlBulkCopyHelper.ConfigureBulkCopy</param>
        /// <param name="cancellationToken">Do you like to have the option to cancel the operation?</param>
        /// <returns>Number of rows in the table</returns>
        // Preferred over the IAsyncEnumerable overload for types that implement both, like an EF Core DbSet
        [OverloadResolutionPriority(1)]
        public ValueTask<long> BulkReplaceAsync<T>(string tableName, IEnumerable<T> entities, Func<PropertyInfo, string>? columnNameFunc = null,
            Action<SqlBulkReplaceOptions>? configure = null, int timeout = 30, SqlBulkCopyOptions sqlBulkCopyOptions = SqlBulkCopyOptions.Default,
            Action<SqlBulkCopy>? configureBulkCopy = null, CancellationToken cancellationToken = default) where T : class
        {
            return CreateEntityHelper<T>(tableName, columnNameFunc, configureBulkCopy)
                .BulkReplaceAsync(connection, entities, configure, timeout, sqlBulkCopyOptions, cancellationToken);
        }

        /// <summary>
        /// Replaces all rows in the table with the entities, streamed from an IAsyncEnumerable without blocking threads. See SqlBulkCopyHelper.BulkReplaceAsync for how it's done.
        /// Mapping will be done by selecting all public properties and then assuming that their names match the database column names.
        /// A type that implements both IEnumerable and IAsyncEnumerable (like an EF Core DbSet) uses the IEnumerable overload, call .AsAsyncEnumerable() to use this one.
        /// </summary>
        /// <param name="tableName">Table to replace the rows in</param>
        /// <param name="entities">All the rows the table should have</param>
        /// <param name="columnNameFunc">How to map from PropertyInfo to the desired column name. Default just PropertyInfo.Name</param>
        /// <param name="configure">Configures the swap, for example WaitAtLowPriority</param>
        /// <param name="timeout">Number of seconds for each step to complete before it times out, like the bulk copy, creating the indexes and the swap. 0 equals no timeout. Default 30 seconds</param>
        /// <param name="sqlBulkCopyOptions">Options for the SqlBulkCopy to the incoming table. TableLock is always used, since no one else uses that table.</param>
        /// <param name="configureBulkCopy">Configure the SqlBulkCopy instance, for example BatchSize, NotifyAfter or SqlRowsCopied. See SqlBulkCopyHelper.ConfigureBulkCopy</param>
        /// <param name="cancellationToken">Cancels the operation. It's also passed to the source when it's enumerated.</param>
        /// <returns>Number of rows in the table</returns>
        public ValueTask<long> BulkReplaceAsync<T>(string tableName, IAsyncEnumerable<T> entities, Func<PropertyInfo, string>? columnNameFunc = null,
            Action<SqlBulkReplaceOptions>? configure = null, int timeout = 30, SqlBulkCopyOptions sqlBulkCopyOptions = SqlBulkCopyOptions.Default,
            Action<SqlBulkCopy>? configureBulkCopy = null, CancellationToken cancellationToken = default) where T : class
        {
            return CreateEntityHelper<T>(tableName, columnNameFunc, configureBulkCopy)
                .BulkReplaceAsync(connection, entities, configure, timeout, sqlBulkCopyOptions, cancellationToken);
        }

        /// <summary>
        /// Replaces all rows in the table with the rows in the DataTable, without readers ever seeing the table empty or half-filled. See SqlBulkCopyHelper.BulkReplaceAsync for how it's done.
        /// The columns are mapped by name, not by position. Deleted rows are skipped, like SqlBulkCopy.WriteToServer(DataTable) does.
        /// </summary>
        /// <param name="tableName">Table to replace the rows in</param>
        /// <param name="dataTable">All the rows the table should have</param>
        /// <param name="configure">Configures the swap, for example WaitAtLowPriority</param>
        /// <param name="timeout">Number of seconds for each step to complete before it times out, like the bulk copy, creating the indexes and the swap. 0 equals no timeout. Default 30 seconds</param>
        /// <param name="sqlBulkCopyOptions">Options for the SqlBulkCopy to the incoming table. TableLock is always used, since no one else uses that table.</param>
        /// <param name="configureBulkCopy">Configure the SqlBulkCopy instance, for example BatchSize, NotifyAfter or SqlRowsCopied. It's called after the column mappings are added, so they can be changed.</param>
        /// <param name="cancellationToken">Do you like to have the option to cancel the operation?</param>
        /// <returns>Number of rows in the table</returns>
        /// <exception cref="InvalidOperationException">If the table doesn't exist, or a column in the DataTable doesn't exist in it</exception>
        public ValueTask<long> BulkReplaceAsync(string tableName, DataTable dataTable, Action<SqlBulkReplaceOptions>? configure = null,
            int timeout = 30, SqlBulkCopyOptions sqlBulkCopyOptions = SqlBulkCopyOptions.Default, Action<SqlBulkCopy>? configureBulkCopy = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(dataTable);

            var columnNames = dataTable.Columns.Cast<DataColumn>().Select(x => x.ColumnName);
            return ReplaceByColumnNameAsync(connection, tableName, columnNames, configureBulkCopy, (bulkCopy, ct) => bulkCopy.WriteToServerAsync(dataTable, ct),
                configure, timeout, sqlBulkCopyOptions, cancellationToken);
        }

        /// <summary>
        /// Replaces all rows in the table with the rows from the reader, without readers of the table ever seeing it empty or half-filled. See SqlBulkCopyHelper.BulkReplaceAsync for how it's done.
        /// For example a SqlDataReader from another database, so the rows are streamed without being loaded into memory.
        /// The columns are mapped by name, not by position. The reader is not disposed.
        /// A reader on the same connection only works with MultipleActiveResultSets, so use another connection.
        /// </summary>
        /// <param name="tableName">Table to replace the rows in</param>
        /// <param name="reader">All the rows the table should have</param>
        /// <param name="configure">Configures the swap, for example WaitAtLowPriority</param>
        /// <param name="timeout">Number of seconds for each step to complete before it times out, like the bulk copy, creating the indexes and the swap. 0 equals no timeout. Default 30 seconds</param>
        /// <param name="sqlBulkCopyOptions">Options for the SqlBulkCopy to the incoming table. TableLock is always used, since no one else uses that table.</param>
        /// <param name="configureBulkCopy">Configure the SqlBulkCopy instance, for example BatchSize, NotifyAfter or SqlRowsCopied. It's called after the column mappings are added, so they can be changed.</param>
        /// <param name="cancellationToken">Do you like to have the option to cancel the operation?</param>
        /// <returns>Number of rows in the table</returns>
        /// <exception cref="InvalidOperationException">If the table doesn't exist, or a column in the reader doesn't exist in it</exception>
        public ValueTask<long> BulkReplaceAsync(string tableName, DbDataReader reader, Action<SqlBulkReplaceOptions>? configure = null,
            int timeout = 30, SqlBulkCopyOptions sqlBulkCopyOptions = SqlBulkCopyOptions.Default, Action<SqlBulkCopy>? configureBulkCopy = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(reader);

            var columnNames = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName);
            return ReplaceByColumnNameAsync(connection, tableName, columnNames, configureBulkCopy, (bulkCopy, ct) => bulkCopy.WriteToServerAsync(reader, ct),
                configure, timeout, sqlBulkCopyOptions, cancellationToken);
        }
    }

    private static ValueTask<long> ReplaceByColumnNameAsync(SqlConnection connection, string tableName, IEnumerable<string> columnNames,
        Action<SqlBulkCopy>? configureBulkCopy, Func<SqlBulkCopy, CancellationToken, Task> writeRows,
        Action<SqlBulkReplaceOptions>? configure, int timeout, SqlBulkCopyOptions sqlBulkCopyOptions, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tableName)) throw new ArgumentNullException(nameof(tableName));

        var tableNameParts = SplitMultipartName(tableName);
        var columnMappings = columnNames.Select(x => (x, Quote(x))).ToList();

        return BulkReplace.ReplaceAsync(connection, QuoteMultipartName(tableNameParts), tableNameParts, columnMappings, configureBulkCopy, writeRows,
            configure, timeout, sqlBulkCopyOptions, cancellationToken);
    }

    private static SqlBulkCopyHelper<T> CreateEntityHelper<T>(string tableName, Func<PropertyInfo, string>? columnNameFunc, Action<SqlBulkCopy>? configureBulkCopy)
        where T : class
    {
        var helper = new SqlBulkCopyHelper<T>(tableName)
            .UseBracketQuoting()
            .MapAllPublicProperties(columnNameFunc);

        return configureBulkCopy is null ? helper : helper.ConfigureBulkCopy(configureBulkCopy);
    }

    private static SqlBulkCopyHelper<T> CreateValueHelper<T>(string tableName, string columnName, Action<SqlBulkCopy>? configureBulkCopy)
    {
        var helper = new SqlBulkCopyHelper<T>(tableName)
            .UseBracketQuoting()
            .Map(columnName);

        return configureBulkCopy is null ? helper : helper.ConfigureBulkCopy(configureBulkCopy);
    }
}
