using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace SqlBulkCopyHelper;

/// <summary>
/// The structure of a table, read from the catalog views, so a copy can be created that ALTER TABLE SWITCH accepts.
/// SWITCH requires the same columns, clustered index, nonclustered indexes, CHECK constraints and foreign keys, and validates them itself.
/// Things that SWITCH doesn't check and that don't affect the rows, like triggers, permissions and index names, are not copied, since the table keeps its own.
/// </summary>
internal sealed class TableStructure
{
    private const byte Heap = 0, Clustered = 1, Nonclustered = 2, ClusteredColumnstore = 5, NonclusteredColumnstore = 6;

    private readonly List<string> _columns;
    private readonly List<Index> _indexes;
    private readonly List<Constraint> _constraints;
    private readonly string? _lobDataSpace;

    private TableStructure(string schemaName, string tableName, List<string> columns, bool hasIdentity, List<Index> indexes, List<Constraint> constraints, string? lobDataSpace)
    {
        SchemaName = schemaName;
        TableName = tableName;
        _columns = columns;
        HasIdentity = hasIdentity;
        _indexes = indexes;
        _constraints = constraints;
        _lobDataSpace = lobDataSpace;
    }

    public string SchemaName { get; }
    public string TableName { get; }
    public string QuotedName => Quote(SchemaName) + "." + Quote(TableName);
    public bool HasIdentity { get; }

    private sealed record Index(string Name, byte Type, bool IsUnique, bool IsPrimaryKey, bool IsUniqueConstraint, string? Filter, bool IgnoreDupKey, bool IsDisabled,
        string DataSpace, string? Compression, List<IndexColumn> Columns);

    private sealed record IndexColumn(string Name, bool IsDescending, bool IsIncluded);

    // Definition is the constraint after ADD, like CHECK (...) or FOREIGN KEY (...) REFERENCES ...
    private sealed record Constraint(string Definition, bool IsTrusted, bool IsDisabled);

    /// <summary>
    /// Creates the table with the columns and the clustered index. The clustered index is created before the rows are loaded, since building it afterwards would rewrite the table.
    /// </summary>
    public string CreateTableScript(string quotedName)
    {
        var heap = _indexes.Single(x => x.Type is Heap or Clustered or ClusteredColumnstore);

        var sb = new StringBuilder();
        sb.Append("CREATE TABLE ").AppendLine(quotedName);
        sb.AppendLine("(");
        sb.Append('\t').AppendJoin("," + Environment.NewLine + "\t", _columns).AppendLine();
        sb.Append(") ON ").Append(Quote(heap.DataSpace));

        if (_lobDataSpace is not null)
        {
            sb.Append(" TEXTIMAGE_ON ").Append(Quote(_lobDataSpace));
        }

        if (heap.Type == Heap)
        {
            AppendWith(sb, heap);
        }

        sb.AppendLine(";");

        if (heap.Type != Heap)
        {
            AppendIndex(sb, quotedName, heap);
        }

        return sb.ToString();
    }

    /// <summary>
    /// The nonclustered indexes, CHECK constraints and foreign keys, which are created after the rows are loaded.
    /// SqlBulkCopy doesn't check constraints by default, and SWITCH requires a trusted constraint if the table's is, so they are added WITH CHECK when the table's is trusted.
    /// Disabled constraints are added first and then disabled with NOCHECK CONSTRAINT ALL, since the constraints get generated names that can't be referenced.
    /// </summary>
    public string CreateIndexesAndConstraintsScript(string quotedName)
    {
        var sb = new StringBuilder();

        foreach (var index in _indexes.Where(x => x.Type is Nonclustered or NonclusteredColumnstore))
        {
            AppendIndex(sb, quotedName, index);
        }

        var disabled = _constraints.Where(x => x.IsDisabled).ToList();
        foreach (var constraint in disabled)
        {
            sb.Append("ALTER TABLE ").Append(quotedName).Append(" WITH NOCHECK ADD ").Append(constraint.Definition).AppendLine(";");
        }

        if (disabled.Count > 0)
        {
            sb.Append("ALTER TABLE ").Append(quotedName).AppendLine(" NOCHECK CONSTRAINT ALL;");
        }

        foreach (var constraint in _constraints.Where(x => !x.IsDisabled))
        {
            sb.Append("ALTER TABLE ").Append(quotedName).Append(constraint.IsTrusted ? " WITH CHECK ADD " : " WITH NOCHECK ADD ").Append(constraint.Definition).AppendLine(";");
        }

        return sb.ToString();
    }

    private static void AppendIndex(StringBuilder sb, string quotedName, Index index)
    {
        var clustered = index.Type is Clustered or ClusteredColumnstore ? "CLUSTERED" : "NONCLUSTERED";
        var keys = string.Join(", ", index.Columns.Where(x => !x.IsIncluded).Select(x => Quote(x.Name) + (x.IsDescending ? " DESC" : "")));

        if (index.IsPrimaryKey || index.IsUniqueConstraint)
        {
            // Without a name, so the constraint gets a generated name that is unique in the schema
            sb.Append("ALTER TABLE ").Append(quotedName).Append(" ADD ").Append(index.IsPrimaryKey ? "PRIMARY KEY " : "UNIQUE ").Append(clustered)
                .Append(" (").Append(keys).Append(')');
        }
        else if (index.Type is ClusteredColumnstore or NonclusteredColumnstore)
        {
            sb.Append("CREATE ").Append(clustered).Append(" COLUMNSTORE INDEX ").Append(Quote(index.Name)).Append(" ON ").Append(quotedName);

            if (index.Type == NonclusteredColumnstore)
            {
                sb.Append(" (").AppendJoin(", ", index.Columns.Select(x => Quote(x.Name))).Append(')');
            }
        }
        else
        {
            sb.Append("CREATE ").Append(index.IsUnique ? "UNIQUE " : "").Append(clustered).Append(" INDEX ").Append(Quote(index.Name))
                .Append(" ON ").Append(quotedName).Append(" (").Append(keys).Append(')');

            var included = index.Columns.Where(x => x.IsIncluded).Select(x => Quote(x.Name)).ToList();
            if (included.Count > 0)
            {
                sb.Append(" INCLUDE (").AppendJoin(", ", included).Append(')');
            }
        }

        if (index.Filter is not null)
        {
            sb.Append(" WHERE ").Append(index.Filter);
        }

        AppendWith(sb, index);
        sb.Append(" ON ").Append(Quote(index.DataSpace)).AppendLine(";");

        if (index.IsDisabled)
        {
            sb.Append("ALTER INDEX ").Append(Quote(index.Name)).Append(" ON ").Append(quotedName).AppendLine(" DISABLE;");
        }
    }

    private static void AppendWith(StringBuilder sb, Index index)
    {
        var options = new List<string>();
        if (index.IgnoreDupKey)
        {
            options.Add("IGNORE_DUP_KEY = ON");
        }

        // NONE and COLUMNSTORE are the defaults for rowstore and columnstore
        if (index.Compression is not null and not "NONE" and not "COLUMNSTORE")
        {
            options.Add("DATA_COMPRESSION = " + index.Compression);
        }

        if (options.Count > 0)
        {
            sb.Append(" WITH (").AppendJoin(", ", options).Append(')');
        }
    }

    /// <summary>
    /// Reads the structure of a table in the current database.
    /// </summary>
    /// <exception cref="InvalidOperationException">If the table doesn't exist</exception>
    /// <exception cref="NotSupportedException">If the table has something that ALTER TABLE SWITCH or this copy doesn't support</exception>
    public static async Task<TableStructure> ReadAsync(SqlConnection connection, string tableName, int timeout, CancellationToken cancellationToken)
    {
        // OBJECT_ID parses the multipart name, so the name is never concatenated into SQL
        const string sql = """
            DECLARE @ObjectId int = OBJECT_ID(@TableName);

            SELECT
                SCHEMA_NAME(T.schema_id),
                T.name,
                LOB.name,
                CASE
                    WHEN T.is_memory_optimized = 1 THEN 'is memory-optimized'
                    WHEN T.temporal_type <> 0 THEN 'is a temporal table'
                    WHEN T.is_node = 1 OR T.is_edge = 1 THEN 'is a graph table'
                    WHEN T.is_tracked_by_cdc = 1 THEN 'is tracked by Change Data Capture'
                    WHEN EXISTS (SELECT 1 FROM sys.change_tracking_tables CTT WHERE CTT.object_id = T.object_id) THEN 'has Change Tracking enabled, which ALTER TABLE SWITCH doesn''t allow'
                    WHEN EXISTS (SELECT 1 FROM sys.fulltext_indexes FI WHERE FI.object_id = T.object_id) THEN 'has a full-text index'
                    WHEN EXISTS (SELECT 1 FROM sys.indexes I JOIN sys.data_spaces DS ON DS.data_space_id = I.data_space_id WHERE I.object_id = T.object_id AND DS.type = 'PS') THEN 'is partitioned'
                END,
                (SELECT TOP (1) FK.name FROM sys.foreign_keys FK WHERE FK.referenced_object_id = T.object_id ORDER BY FK.name)
            FROM sys.tables T
            LEFT JOIN sys.data_spaces LOB ON LOB.data_space_id = T.lob_data_space_id
            WHERE T.object_id = @ObjectId;

            SELECT
                C.name,
                TY.name,
                CASE WHEN TY.is_user_defined = 1 THEN SCHEMA_NAME(TY.schema_id) END,
                C.max_length,
                C.precision,
                C.scale,
                C.collation_name,
                C.is_nullable,
                C.is_identity,
                CAST(IC.seed_value AS decimal(38, 0)),
                CAST(IC.increment_value AS decimal(38, 0)),
                CC.definition,
                CC.is_persisted,
                DC.definition,
                C.is_rowguidcol,
                C.is_sparse,
                CASE
                    WHEN C.is_filestream = 1 THEN 'is FILESTREAM'
                    WHEN C.is_column_set = 1 THEN 'is a column set'
                    WHEN C.xml_collection_id <> 0 THEN 'is typed XML'
                    WHEN C.generated_always_type <> 0 THEN 'is GENERATED ALWAYS'
                    WHEN C.encryption_type IS NOT NULL THEN 'is encrypted with Always Encrypted'
                END
            FROM sys.columns C
            JOIN sys.types TY ON TY.user_type_id = C.user_type_id
            LEFT JOIN sys.identity_columns IC ON IC.object_id = C.object_id AND IC.column_id = C.column_id
            LEFT JOIN sys.computed_columns CC ON CC.object_id = C.object_id AND CC.column_id = C.column_id
            LEFT JOIN sys.default_constraints DC ON DC.parent_object_id = C.object_id AND DC.parent_column_id = C.column_id
            WHERE C.object_id = @ObjectId
            ORDER BY C.column_id;

            SELECT
                I.index_id,
                I.name,
                I.type,
                I.is_unique,
                I.is_primary_key,
                I.is_unique_constraint,
                I.filter_definition,
                I.ignore_dup_key,
                I.is_disabled,
                DS.name,
                P.data_compression_desc
            FROM sys.indexes I
            JOIN sys.data_spaces DS ON DS.data_space_id = I.data_space_id
            OUTER APPLY (SELECT P.data_compression_desc FROM sys.partitions P WHERE P.object_id = I.object_id AND P.index_id = I.index_id AND P.partition_number = 1) P
            WHERE I.object_id = @ObjectId AND I.is_hypothetical = 0
            ORDER BY I.index_id;

            SELECT IC.index_id, C.name, IC.is_descending_key, IC.is_included_column
            FROM sys.index_columns IC
            JOIN sys.columns C ON C.object_id = IC.object_id AND C.column_id = IC.column_id
            WHERE IC.object_id = @ObjectId
            ORDER BY IC.index_id, CASE WHEN IC.key_ordinal = 0 THEN 1 ELSE 0 END, IC.key_ordinal, IC.index_column_id;

            SELECT CONCAT('CHECK ', CK.definition), CK.is_not_trusted, CK.is_disabled
            FROM sys.check_constraints CK
            WHERE CK.parent_object_id = @ObjectId
            ORDER BY CK.name;

            SELECT
                FK.object_id,
                QUOTENAME(OBJECT_SCHEMA_NAME(FK.referenced_object_id)) + '.' + QUOTENAME(OBJECT_NAME(FK.referenced_object_id)),
                FK.delete_referential_action_desc,
                FK.update_referential_action_desc,
                FK.is_not_trusted,
                FK.is_disabled
            FROM sys.foreign_keys FK
            WHERE FK.parent_object_id = @ObjectId
            ORDER BY FK.name;

            SELECT FKC.constraint_object_id, PC.name, RC.name
            FROM sys.foreign_key_columns FKC
            JOIN sys.foreign_keys FK ON FK.object_id = FKC.constraint_object_id
            JOIN sys.columns PC ON PC.object_id = FKC.parent_object_id AND PC.column_id = FKC.parent_column_id
            JOIN sys.columns RC ON RC.object_id = FKC.referenced_object_id AND RC.column_id = FKC.referenced_column_id
            WHERE FK.parent_object_id = @ObjectId
            ORDER BY FKC.constraint_object_id, FKC.constraint_column_id;
            """;

        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandTimeout = timeout;
            command.CommandText = sql;
            command.Parameters.Add(new SqlParameter("@TableName", SqlDbType.NVarChar, 4000) { Value = tableName });

            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                return await ReadAsync(reader, tableName, connection.Database, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task<TableStructure> ReadAsync(SqlDataReader reader, string tableName, string database, CancellationToken cancellationToken)
    {
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"The table '{tableName}' was not found in the database '{database}'.");
        }

        var schemaName = reader.GetString(0);
        var name = reader.GetString(1);
        var quotedName = Quote(schemaName) + "." + Quote(name);
        var lobDataSpace = reader.IsDBNull(2) ? null : reader.GetString(2);

        if (!reader.IsDBNull(3))
        {
            throw NotSupported(quotedName, reader.GetString(3));
        }

        if (!reader.IsDBNull(4))
        {
            throw NotSupported(quotedName, $"is referenced by the foreign key {Quote(reader.GetString(4))}, so its rows can't be switched out");
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        var columns = new List<string>();
        var hasIdentity = false;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var columnName = reader.GetString(0);
            if (!reader.IsDBNull(16))
            {
                throw NotSupported(quotedName, $"has the column {Quote(columnName)} that {reader.GetString(16)}");
            }

            hasIdentity |= reader.GetBoolean(8);
            columns.Add(GetColumnDefinition(reader, columnName));
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        var indexes = new Dictionary<int, Index>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var indexName = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var type = reader.GetByte(2);
            var isDisabled = reader.GetBoolean(8);

            if (type is not (Heap or Clustered or Nonclustered or ClusteredColumnstore or NonclusteredColumnstore))
            {
                throw NotSupported(quotedName, $"has the index {Quote(indexName)} that is not a rowstore or columnstore index");
            }

            if (isDisabled && (type != Nonclustered || reader.GetBoolean(4) || reader.GetBoolean(5)))
            {
                throw NotSupported(quotedName, $"has the disabled index {Quote(indexName)}");
            }

            indexes.Add(reader.GetInt32(0), new Index(
                indexName,
                type,
                IsUnique: reader.GetBoolean(3),
                IsPrimaryKey: reader.GetBoolean(4),
                IsUniqueConstraint: reader.GetBoolean(5),
                Filter: reader.IsDBNull(6) ? null : reader.GetString(6),
                IgnoreDupKey: reader.GetBoolean(7),
                isDisabled,
                DataSpace: reader.GetString(9),
                Compression: reader.IsDBNull(10) ? null : reader.GetString(10),
                Columns: []));
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            indexes[reader.GetInt32(0)].Columns.Add(new IndexColumn(reader.GetString(1), reader.GetBoolean(2), reader.GetBoolean(3)));
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        var constraints = new List<Constraint>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            constraints.Add(new Constraint(reader.GetString(0), IsTrusted: !reader.GetBoolean(1), IsDisabled: reader.GetBoolean(2)));
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        var foreignKeys = new List<(int Id, string Referenced, string OnDelete, string OnUpdate, bool IsTrusted, bool IsDisabled)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            foreignKeys.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), !reader.GetBoolean(4), reader.GetBoolean(5)));
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        var foreignKeyColumns = new List<(int Id, string Column, string Referenced)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            foreignKeyColumns.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
        }

        foreach (var foreignKey in foreignKeys)
        {
            var keyColumns = foreignKeyColumns.Where(x => x.Id == foreignKey.Id).ToList();
            var definition = $"FOREIGN KEY ({string.Join(", ", keyColumns.Select(x => Quote(x.Column)))}) " +
                             $"REFERENCES {foreignKey.Referenced} ({string.Join(", ", keyColumns.Select(x => Quote(x.Referenced)))}) " +
                             $"ON DELETE {foreignKey.OnDelete.Replace('_', ' ')} ON UPDATE {foreignKey.OnUpdate.Replace('_', ' ')}";

            constraints.Add(new Constraint(definition, foreignKey.IsTrusted, foreignKey.IsDisabled));
        }

        return new TableStructure(schemaName, name, columns, hasIdentity, indexes.Values.ToList(), constraints, lobDataSpace);
    }

    private static string GetColumnDefinition(SqlDataReader reader, string columnName)
    {
        var sb = new StringBuilder(Quote(columnName));
        var isNullable = reader.GetBoolean(7);

        if (!reader.IsDBNull(11))
        {
            sb.Append(" AS ").Append(reader.GetString(11));
            if (reader.GetBoolean(12))
            {
                sb.Append(" PERSISTED");
                if (!isNullable)
                {
                    sb.Append(" NOT NULL");
                }
            }

            return sb.ToString();
        }

        sb.Append(' ').Append(GetTypeName(reader));

        // An alias type can't have COLLATE, it always has the collation of the database
        if (!reader.IsDBNull(6) && reader.IsDBNull(2))
        {
            sb.Append(" COLLATE ").Append(reader.GetString(6));
        }

        if (reader.GetBoolean(15))
        {
            sb.Append(" SPARSE");
        }

        if (!reader.IsDBNull(13))
        {
            sb.Append(" DEFAULT ").Append(reader.GetString(13));
        }

        if (reader.GetBoolean(8))
        {
            sb.Append(" IDENTITY(").Append(reader.GetDecimal(9).ToString(CultureInfo.InvariantCulture))
                .Append(", ").Append(reader.GetDecimal(10).ToString(CultureInfo.InvariantCulture)).Append(')');
        }

        if (reader.GetBoolean(14))
        {
            sb.Append(" ROWGUIDCOL");
        }

        sb.Append(isNullable ? " NULL" : " NOT NULL");
        return sb.ToString();
    }

    private static string GetTypeName(SqlDataReader reader)
    {
        var typeName = reader.GetString(1);

        // An alias type, like CREATE TYPE dbo.Sku FROM varchar(20), has its length in the type
        if (!reader.IsDBNull(2))
        {
            return Quote(reader.GetString(2)) + "." + Quote(typeName);
        }

        var maxLength = reader.GetInt16(3);
        return typeName switch
        {
            "varchar" or "char" or "varbinary" or "binary" => $"{typeName}({(maxLength == -1 ? "max" : maxLength.ToString(CultureInfo.InvariantCulture))})",
            "nvarchar" or "nchar" => $"{typeName}({(maxLength == -1 ? "max" : (maxLength / 2).ToString(CultureInfo.InvariantCulture))})",
            "decimal" or "numeric" => $"{typeName}({reader.GetByte(4).ToString(CultureInfo.InvariantCulture)}, {reader.GetByte(5).ToString(CultureInfo.InvariantCulture)})",
            "datetime2" or "time" or "datetimeoffset" => $"{typeName}({reader.GetByte(5).ToString(CultureInfo.InvariantCulture)})",
            _ => typeName
        };
    }

    private static NotSupportedException NotSupported(string quotedName, string reason) =>
        new($"BulkReplaceAsync doesn't support {quotedName}, since it {reason}.");

    private static string Quote(string name) => "[" + name.Replace("]", "]]") + "]";
}
