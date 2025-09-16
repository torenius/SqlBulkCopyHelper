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

    public SqlBulkCopyHelper(string tableName)
    {
        _tableName = tableName;
    }
    
    public DbDataReader GetDataReader(IEnumerable<TEntity> entities) =>
        new DisguisedDataReader<TEntity>(_columnDefinitions, entities);

    public DataTable GetDataTable(IEnumerable<TEntity> entities)
    {
        var dt = new DataTable(_tableName);
        dt.Load(GetDataReader(entities));
        return dt;
    }
    
    public async ValueTask<ulong> BulkInsertAsync(SqlConnection connection, IEnumerable<TEntity> entities,
        int timeout = 30, SqlBulkCopyOptions sqlBulkCopyOptions = SqlBulkCopyOptions.Default, SqlTransaction? sqlTransaction = null, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return await new ValueTask<ulong>(Task.FromCanceled<ulong>(cancellationToken));
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
        
        foreach (var columnName in GetColumnNames())
        {
            bulkCopy.ColumnMappings.Add(columnName, QuoteName(columnName));
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

        return (ulong)bulkCopy.RowsCopied64;
    }
    
    public IEnumerable<string> GetColumnNames() => _columnDefinitions.Select(x => x.ColumnName);
    
    private static readonly Dictionary<Type, string> ColumnType = new()
    {
        {typeof(bool), "BIT"},
        {typeof(byte), "TINYINT"},
        {typeof(sbyte), "SMALLINT"},
        {typeof(short), "SMALLINT"},
        {typeof(ushort), "INT"},
        {typeof(int), "INT"},
        {typeof(uint), "BIGINT"},
        {typeof(long), "BIGINT"},
        {typeof(ulong), "BIGINT"},
        {typeof(decimal), "NUMERIC"},
        {typeof(double), "FLOAT"},
        {typeof(DateTime), "DATETIME2"},
        {typeof(Guid), "UNIQUEIDENTIFIER"},
        {typeof(string), "NVARCHAR"},
        {typeof(char), "NCHAR"},
        {typeof(char[]), "NVARCHAR"},
        {typeof(byte[]), "VARBINARY"},
    };

    public IEnumerable<string> GetColumnSchemaDefinition()
    {
        var sb = new StringBuilder();
        foreach (var columnDefinition in _columnDefinitions)
        {
            sb.Clear();
            sb.Append(QuoteName(columnDefinition.ColumnName));

            if (!ColumnType.TryGetValue(columnDefinition.Type, out var columnType))
            {
                columnType = "NVARCHAR(MAX)";
            }
            
            sb.Append(' ').Append(columnType);

            if (columnDefinition.Type == typeof(string) || columnDefinition.Type == typeof(byte[]) || columnDefinition.Type == typeof(char[]))
            {
                if (columnDefinition.ColumnSize > 0 && columnDefinition.ColumnSize <= 8000)
                {
                    sb.Append('(').Append(columnDefinition.ColumnSize).Append(')');
                }
                else
                {
                    sb.Append("(MAX)");
                }
            }

            if (columnDefinition.Type == typeof(decimal))
            {
                sb.Append('(').Append(columnDefinition.NumericPrecision);
                if (columnDefinition.NumericScale > 0)
                {
                    sb.Append(", ").Append(columnDefinition.NumericScale);
                }

                sb.Append(')');
            } 

            if (!columnDefinition.Nullable)
            {
                sb.Append(" NOT");
            }

            sb.Append(" NULL");

            yield return sb.ToString();
        }
    }

    /// <summary>
    /// This is more for creating a staging table.
    /// </summary>
    /// <returns>A script that can be run against the database to create a staging table.</returns>
    public string CreateTableScript()
    {
        var sb = new StringBuilder();
        sb.Append("CREATE TABLE ").AppendLine(string.Join(".", _tableName.Split('.').Select(QuoteName)));
        sb.AppendLine("(");

        var columns = GetColumnSchemaDefinition().ToList();

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

    public SqlBulkCopyHelper<TEntity> Map<TProperty>(string columnName, Func<TEntity, TProperty> propertyGetter)
    {
        return AddOrUpdateColumn(new DisguisedColumnDefinition<TEntity>
        {
            ColumnName = columnName,
            Type = typeof(TProperty),
            PropertyGetter = (entity) => (object)propertyGetter(entity)!
        });
    }
    
    public SqlBulkCopyHelper<TEntity> MapNullable<TProperty>(string columnName, Func<TEntity, TProperty?> propertyGetter)
    {
        return AddOrUpdateColumn(new DisguisedColumnDefinition<TEntity>
        {
            ColumnName = columnName,
            Type = typeof(TProperty),
            PropertyGetter = (entity) => (object)propertyGetter(entity)!,
            Nullable = true
        });
    }
    
    public SqlBulkCopyHelper<TEntity> Map(string columnName, Func<TEntity, object> propertyGetter, Type propertyType)
    {
        return AddOrUpdateColumn(new DisguisedColumnDefinition<TEntity>
        {
            ColumnName = columnName,
            Type = propertyType,
            PropertyGetter = (entity) => propertyGetter(entity)!
        });
    }
    
    public SqlBulkCopyHelper<TEntity> MapNullable(string columnName, Func<TEntity, object?> propertyGetter, Type propertyType)
    {
        return AddOrUpdateColumn(new DisguisedColumnDefinition<TEntity>
        {
            ColumnName = columnName,
            Type = propertyType,
            PropertyGetter = (entity) => propertyGetter(entity)!,
            Nullable = true
        });
    }
    
    public SqlBulkCopyHelper<TEntity> MapDecimal(string columnName, Func<TEntity, decimal> propertyGetter, int numericPrecision = 18, int numericScale = 0)
    {
        return AddOrUpdateColumn(new DisguisedColumnDefinition<TEntity>
        {
            ColumnName = columnName,
            Type = typeof(decimal),
            PropertyGetter = (entity) => (object)propertyGetter(entity),
            NumericPrecision = numericPrecision,
            NumericScale = numericScale
        });
    }

    public SqlBulkCopyHelper<TEntity> MapString(string columnName, Func<TEntity, string> propertyGetter, int length = -1)
    {
        return AddOrUpdateColumn(new DisguisedColumnDefinition<TEntity>
        {
            ColumnName = columnName,
            Type = typeof(string),
            PropertyGetter = (entity) => (object)propertyGetter(entity),
            ColumnSize = length
        });
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
    
    private SqlBulkCopyHelper<TEntity> AddOrUpdateColumn(DisguisedColumnDefinition<TEntity> disguisedColumnDefinition)
    {
        var columnDefinition = _columnDefinitions.FirstOrDefault(x => x.ColumnName == disguisedColumnDefinition.ColumnName);
        if (columnDefinition is not null)
        {
            _columnDefinitions.Remove(columnDefinition);
        }
        
        _columnDefinitions.Add(disguisedColumnDefinition);

        return this;
    }
}