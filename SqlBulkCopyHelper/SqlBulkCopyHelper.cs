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
    /// <param name="connection">SqlConnection to connect to. If It's closed, this code will open it, do the insert then close it. If it was open it will be kept open</param>
    /// <param name="entities">All the entities that will be inserted</param>
    /// <param name="createTableIfNotExists">If true it will first make a call to creating the table that "CreateTableScript" generates</param>
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

        SqlBulkCopy bulkCopy;
        var commitTransaction = false;
        if (sqlBulkCopyOptions == SqlBulkCopyOptions.Default && sqlTransaction is null)
        {
            bulkCopy = new SqlBulkCopy(connection);
        }
        else
        {
            if (sqlTransaction is null)
            {
                sqlTransaction = connection.BeginTransaction();
                commitTransaction = true;
            }
            
            bulkCopy = new SqlBulkCopy(connection, sqlBulkCopyOptions, sqlTransaction);
        }
        
        bulkCopy.DestinationTableName = string.Join(".", _tableName.Split('.').Select(QuoteName));
        bulkCopy.BulkCopyTimeout = timeout;
        
        foreach (var columnInfo in GetColumnInfo())
        {
            bulkCopy.ColumnMappings.Add(columnInfo.ColumnName, columnInfo.QuotedColumnName);
        }

        if (createTableIfNotExists)
        {
            var sqlCommand = connection.CreateCommand();
            sqlCommand.CommandText = CreateTableScript();
            await sqlCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        
        await bulkCopy.WriteToServerAsync(GetDataReader(entities), cancellationToken);

        if (commitTransaction)
        {
            sqlTransaction!.Commit();
        }
        
        if (closeConnection)
        {
            connection.Close();
        }

        return bulkCopy.RowsCopied64;
    }
    
    /// <summary>
    /// Can be used to add or change how Types are mapped for the value in SqlBulkCopyHelperColumnInfo.SchemaDefinition
    /// Intention is just to use these mappings for a staging table. They might be on the "bigger" side.
    /// </summary>
    public Dictionary<Type, string> SchemaDefinitionMapping = new()
    {
        {typeof(bool), "bit"},
        {typeof(byte), "tinyint"},
        {typeof(sbyte), "smallint"},
        {typeof(short), "smallint"},
        {typeof(ushort), "int"},
        {typeof(int), "int"},
        {typeof(uint), "bigint"},
        {typeof(long), "bigint"},
        {typeof(ulong), "bigint"},
        {typeof(decimal), "numeric(38,15)"},
        {typeof(double), "float"},
        {typeof(DateTime), "datetime2(7)"},
        {typeof(Guid), "uniqueidentifier"},
        {typeof(string), "nvarchar(max)"},
        {typeof(char), "nchar(1)"},
        {typeof(char[]), "nvarchar(max)"},
        {typeof(byte[]), "varbinary(max)"},
        
        {typeof(bool?), "bit"},
        {typeof(byte?), "tinyint"},
        {typeof(sbyte?), "smallint"},
        {typeof(short?), "smallint"},
        {typeof(ushort?), "int"},
        {typeof(int?), "int"},
        {typeof(uint?), "bigint"},
        {typeof(long?), "bigint"},
        {typeof(ulong?), "bigint"},
        {typeof(decimal?), "numeric(38,15)"},
        {typeof(double?), "float"},
        {typeof(DateTime?), "datetime2(7)"},
        {typeof(Guid?), "uniqueidentifier"},
        {typeof(char?), "nchar(1)"},
    };
    
    /// <returns></returns>
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

            if (!SchemaDefinitionMapping.TryGetValue(columnDefinition.Type, out var columnType))
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
    /// <returns>A script that can be run against the database to create a staging table.</returns>
    public string CreateTableScript(bool columnsAreAlwaysNullable = true)
    {
        var sb = new StringBuilder();
        sb.Append("create table ").AppendLine(string.Join(".", _tableName.Split('.').Select(QuoteName)));
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

        return sb.ToString();
    }

    /// <summary>
    /// Adds a mapping rule where we can infer the type from the lamda.
    /// </summary>
    /// <param name="columnName">Database column name</param>
    /// <param name="propertyGetter">Lambda for getting the value</param>
    /// <typeparam name="TProperty">The Type</typeparam>
    /// <returns>The SqlBulkCopyHelper so you can continue with the builder pattern</returns>
    public SqlBulkCopyHelper<TEntity> Map<TProperty>(string columnName, Func<TEntity, TProperty> propertyGetter)
    {
        return AddOrUpdateColumn(new DisguisedColumnDefinition<TEntity>
        {
            ColumnName = columnName,
            Type = typeof(TProperty),
            PropertyGetter = (entity) => (object)propertyGetter(entity)!
        });
    }
    
    /// <summary>
    /// Adds a mapping rule where we can't infer the type from the lamda.
    /// </summary>
    /// <param name="columnName">Database column name</param>
    /// <param name="propertyGetter">Lambda for getting the value</param>
    /// <param name="propertyType">The Type</param>
    /// <returns>The SqlBulkCopyHelper so you can continue with the builder pattern</returns>
    public SqlBulkCopyHelper<TEntity> Map(string columnName, Func<TEntity, object> propertyGetter, Type propertyType)
    {
        return AddOrUpdateColumn(new DisguisedColumnDefinition<TEntity>
        {
            ColumnName = columnName,
            Type = propertyType,
            PropertyGetter = (entity) => propertyGetter(entity)!
        });
    }

    /// <summary>
    /// Mapping is basically a dictionary where the columnName is the key.
    /// If you for some reason need to remove a mapping.
    /// Use this method. 
    /// </summary>
    /// <param name="columnName">Removes the mapping if it exits. It does not exist, nothing happen</param>
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
    
    private SqlBulkCopyHelper<TEntity> AddOrUpdateColumn(DisguisedColumnDefinition<TEntity> disguisedColumnDefinition)
    {
        RemoveMap(disguisedColumnDefinition.ColumnName);

        var underlyingType = Nullable.GetUnderlyingType(disguisedColumnDefinition.Type);
        if (underlyingType is not null)
        {
            disguisedColumnDefinition.Type = underlyingType;
            disguisedColumnDefinition.Nullable = true;
        }
        
        _columnDefinitions.Add(disguisedColumnDefinition);

        return this;
    }

    /// <summary>
    /// Quote table and column namnes in brackets []
    /// </summary>
    public SqlBulkCopyHelper<TEntity> UseBracketQuoting() => UseQuoting("[", "]");
    
    /// <summary>
    /// Quote table and column names in quotation marks ""
    /// </summary>
    public SqlBulkCopyHelper<TEntity> UseQuotationMarkQuoting() => UseQuoting("\"", "\"");

    /// <summary>
    /// Quote table and column names.
    /// </summary>
    /// <param name="prefixQuote">If the name doesn't start with this value, add it during quoting stage.</param>
    /// <param name="postfixQuote">If the name doesn't end with this value, add it during quoting stage.</param>
    public SqlBulkCopyHelper<TEntity> UseQuoting(string prefixQuote, string postfixQuote)
    {
        _useQuoting = true;
        _prefixQuote = prefixQuote;
        _postfixQuote = postfixQuote;
        return this;
    }
    
    private bool _useQuoting;
    private string _prefixQuote = string.Empty;
    private string _postfixQuote = string.Empty;

    private string QuoteName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new Exception("Can't map null or empty string");

        if (!_useQuoting) return name;

        if (!name.StartsWith(_prefixQuote))
        {
            name = _prefixQuote + name;
        }

        if (!name.EndsWith(_postfixQuote))
        {
            name += _postfixQuote;
        }
        
        return name;
    }
}