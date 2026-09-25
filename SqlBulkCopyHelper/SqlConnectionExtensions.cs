using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace SqlBulkCopyHelper;

/// <summary>
/// Extension methods for bulk inserting directly on a SqlConnection.
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
