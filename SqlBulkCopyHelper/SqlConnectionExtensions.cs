using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace SqlBulkCopyHelper;

public static class SqlConnectionExtensions
{
    public static ValueTask<ulong> BulkInserAsync<T>(this SqlConnection connection, string tableName, IEnumerable<T> entities,
        int timeout = 30, SqlBulkCopyOptions sqlBulkCopyOptions = SqlBulkCopyOptions.Default, SqlTransaction? sqlTransaction = null, CancellationToken cancellationToken = default) where T : class
    {
        var helper = new SqlBulkCopyHelper<T>(tableName)
            .UseBracketQuoting()
            .MapAllPublicProperties();
        
        return helper.BulkInsertAsync(connection, entities, timeout, sqlBulkCopyOptions, sqlTransaction, cancellationToken);
    }
    
    public static ValueTask<ulong> BulkInserAsync<T>(this SqlConnection connection, string tableName, string columnName, IEnumerable<T> entities,
        int timeout = 30, SqlBulkCopyOptions sqlBulkCopyOptions = SqlBulkCopyOptions.Default, SqlTransaction? sqlTransaction = null, CancellationToken cancellationToken = default) where T : struct
    {
        var helper = new SqlBulkCopyHelper<T>(tableName)
            .UseBracketQuoting()
            .Map(columnName, x => x);
        
        return helper.BulkInsertAsync(connection, entities, timeout, sqlBulkCopyOptions, sqlTransaction, cancellationToken);
    }
}