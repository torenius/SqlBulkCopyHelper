using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace SqlBulkCopyHelper;

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
        /// <param name="createTableIfNotExists">If true it will first make a call to creating the table that "CreateTableScript" generates</param>
        /// <param name="timeout">Number of seconds for the operation to complete before it times out. 0 equals no timeout. Default 30 seconds</param>
        /// <param name="sqlBulkCopyOptions">Different options that SqlBulkCopy will consider</param>
        /// <param name="sqlTransaction">If this should be done in a specific transaction or not</param>
        /// <param name="cancellationToken">Do you like to have the option to cancel the operation?</param>
        /// <returns>Number of rows inserted</returns>
        public ValueTask<long> BulkInsertAsync<T>(string tableName, IEnumerable<T> entities, Func<PropertyInfo, string>? columnNameFunc = null, bool createTableIfNotExists = false,
            int timeout = 30, SqlBulkCopyOptions sqlBulkCopyOptions = SqlBulkCopyOptions.Default, SqlTransaction? sqlTransaction = null, CancellationToken cancellationToken = default) where T : class
        {
            var helper = new SqlBulkCopyHelper<T>(tableName)
                .UseBracketQuoting()
                .MapAllPublicProperties(columnNameFunc);
        
            return helper.BulkInsertAsync(connection, entities, createTableIfNotExists, timeout, sqlBulkCopyOptions, sqlTransaction, cancellationToken);
        }

        /// <summary>
        /// Will try to bulk insert all values using SqlBulkCopy.
        /// </summary>
        /// <param name="tableName">Table to insert data into</param>
        /// <param name="columnName">Name of the column to insert the values to</param>
        /// <param name="values">Values to insert</param>
        /// <param name="createTableIfNotExists">If true it will first make a call to creating the table that "CreateTableScript" generates</param>
        /// <param name="timeout">Number of seconds for the operation to complete before it times out. 0 equals no timeout. Default 30 seconds</param>
        /// <param name="sqlBulkCopyOptions">Different options that SqlBulkCopy will consider</param>
        /// <param name="sqlTransaction">If this should be done in a specific transaction or not</param>
        /// <param name="cancellationToken">Do you like to have the option to cancel the operation?</param>
        /// <returns>Number of rows inserted</returns>
        public ValueTask<long> BulkInsertAsync<T>(string tableName, string columnName, IEnumerable<T> values, bool createTableIfNotExists = false,
            int timeout = 30, SqlBulkCopyOptions sqlBulkCopyOptions = SqlBulkCopyOptions.Default, SqlTransaction? sqlTransaction = null, CancellationToken cancellationToken = default) where T : struct
        {
            var helper = new SqlBulkCopyHelper<T>(tableName)
                .UseBracketQuoting()
                .Map(columnName, x => x);
        
            return helper.BulkInsertAsync(connection, values, createTableIfNotExists, timeout, sqlBulkCopyOptions, sqlTransaction, cancellationToken);
        }
    }
}