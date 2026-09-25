using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace SqlBulkCopyHelper;

public class SqlBulkCopyHelper<TEntity>
{
    private readonly string _tableName;
    private readonly List<DisguisedColumnDefinition<TEntity>> _columnDefinitions = [];

    /// <summary>
    /// Helper class easier and more memory efficient do a bulk insert of x amount of rows.
    /// You need to call .Map or other methods to map what values should be inserted and to what column names
    /// </summary>
    /// <param name="tableName">The table to insert to. Could be the main table or a staging/temp table</param>
    /// <exception cref="ArgumentNullException">If table name is null or empty it will throw</exception>
    public SqlBulkCopyHelper(string tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName)) throw new ArgumentNullException(nameof(tableName));
        _tableName = tableName;
    }

    /// <summary>
    /// If you need to convert your list of entities to a DataReader
    /// </summary>
    /// <param name="entities">Entities to convert</param>
    /// <returns>DbDataReader that contains the mapped columns</returns>
    public DbDataReader GetDataReader(IEnumerable<TEntity> entities) =>
        new DisguisedDataReader<TEntity>(_columnDefinitions, entities);

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
    /// <param name="createTableIfNotExists">If true it will first make a call to creating the table that "CreateTableScript" generates. The CREATE TABLE runs in the same transaction as the bulk insert if one is active.</param>
    /// <param name="timeout">Number of seconds for the operation to complete before it times out. 0 equals no timeout. Default 30 seconds</param>
    /// <param name="sqlBulkCopyOptions">Different options that SqlBulkCopy will consider</param>
    /// <param name="sqlTransaction">If this should be done in a specific transaction or not</param>
    /// <param name="cancellationToken">Do you like to have the option to cancel the operation?</param>
    /// <returns>Number of rows inserted</returns>
    public async ValueTask<long> BulkInsertAsync(SqlConnection connection, IEnumerable<TEntity> entities, bool createTableIfNotExists = false,
        int timeout = 30, SqlBulkCopyOptions sqlBulkCopyOptions = SqlBulkCopyOptions.Default, SqlTransaction? sqlTransaction = null, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return await new ValueTask<long>(Task.FromCanceled<long>(cancellationToken));
        }

        var closeConnection = false;
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
            closeConnection = true;
        }

        var commitTransaction = false;
        if (sqlBulkCopyOptions != SqlBulkCopyOptions.Default && sqlTransaction is null)
        {
            sqlTransaction = connection.BeginTransaction();
            commitTransaction = true;
        }

        long rowsCopied;
        try
        {
            using var bulkCopy = sqlTransaction is not null
                ? new SqlBulkCopy(connection, sqlBulkCopyOptions, sqlTransaction)
                : new SqlBulkCopy(connection);

            bulkCopy.DestinationTableName = string.Join(".", _tableName.Split('.').Select(QuoteName));
            bulkCopy.BulkCopyTimeout = timeout;

            foreach (var columnInfo in GetColumnInfo())
            {
                bulkCopy.ColumnMappings.Add(columnInfo.ColumnName, columnInfo.QuotedColumnName);
            }

            if (createTableIfNotExists)
            {
                using var sqlCommand = connection.CreateCommand();
                sqlCommand.Transaction = sqlTransaction;
                sqlCommand.CommandText = CreateTableScript(checkIfTableExists: true);
                await sqlCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await bulkCopy.WriteToServerAsync(GetDataReader(entities), cancellationToken);
            rowsCopied = bulkCopy.RowsCopied64;

            if (commitTransaction)
            {
                sqlTransaction!.Commit();
            }
        }
        catch when (commitTransaction)
        {
            sqlTransaction!.Rollback();
            throw;
        }
        finally
        {
            if (closeConnection)
            {
                connection.Close();
            }
        }

        return rowsCopied;
    }

    /// <summary>
    /// Can be used to add or change how Types are mapped for the value in SqlBulkCopyHelperColumnInfo.SchemaDefinition.
    /// Intention is just to use these mappings for a staging table. They might be on the "bigger" side.
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
        { typeof(ulong), "bigint" },
        { typeof(float), "real" },
        { typeof(double), "float" },
        { typeof(decimal), "numeric(38,15)" },
        { typeof(DateTime), "datetime2(7)" },
        { typeof(DateTimeOffset), "datetimeoffset(7)" },
        { typeof(TimeSpan), "time(7)" },
        { typeof(Guid), "uniqueidentifier" },
        { typeof(string), "nvarchar(max)" },
        { typeof(char), "nchar(1)" },
        { typeof(char[]), "nvarchar(max)" },
        { typeof(byte[]), "varbinary(max)" },

        { typeof(bool?), "bit" },
        { typeof(byte?), "tinyint" },
        { typeof(sbyte?), "smallint" },
        { typeof(short?), "smallint" },
        { typeof(ushort?), "int" },
        { typeof(int?), "int" },
        { typeof(uint?), "bigint" },
        { typeof(long?), "bigint" },
        { typeof(ulong?), "bigint" },
        { typeof(float?), "real" },
        { typeof(double?), "float" },
        { typeof(decimal?), "numeric(38,15)" },
        { typeof(DateTime?), "datetime2(7)" },
        { typeof(DateTimeOffset?), "datetimeoffset(7)" },
        { typeof(TimeSpan?), "time(7)" },
        { typeof(Guid?), "uniqueidentifier" },
        { typeof(char?), "nchar(1)" },
    };

    /// <summary>
    /// Get information about the columns that currently exists in this helper.
    /// </summary>
    /// <param name="columnsAreAlwaysNullable">If true all the SchemaDefinition will be nullable. If false it will be based on if the Type that was provided during mapping is nullable or not.</param>
    /// <returns>SqlBulkCopyHelperColumnInfo</returns>
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
                columnType = "nvarchar(max)";
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
    public string CreateTableScript(bool columnsAreAlwaysNullable = true, bool checkIfTableExists = false)
    {
        var sb = new StringBuilder();
        var schemaTableName = string.Join(".", _tableName.Split('.').Select(QuoteName));

        if (checkIfTableExists)
        {
            sb.Append("IF OBJECT_ID('");
            if (_tableName.Contains('#'))
            {
                sb.Append("tempdb..");
            }

            sb.Append(schemaTableName).AppendLine("') IS NULL");
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
    /// If you for some reason need to remove a mapping, use this method.
    /// </summary>
    /// <param name="columnName">Removes the mapping if it exits. If it does not exist, nothing happens.</param>
    /// <returns>The SqlBulkCopyHelper so you can continue with the builder pattern</returns>
    public SqlBulkCopyHelper<TEntity> RemoveMap(string columnName)
    {
        var columnDefinition = _columnDefinitions.FirstOrDefault(x => x.ColumnName == columnName);
        if (columnDefinition is not null)
        {
            _columnDefinitions.Remove(columnDefinition);
        }

        return this;
    }

    /// <param name="nullable">If null, reference types and Nullable&lt;T&gt; are considered nullable</param>
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

    private bool _useQuoting;
    private static readonly SqlCommandBuilder _quoter = new() { QuotePrefix = "[", QuoteSuffix = "]" };

    private string QuoteName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Can't quote a null or empty name", nameof(name));

        return _useQuoting ? _quoter.QuoteIdentifier(name) : name;
    }
}
