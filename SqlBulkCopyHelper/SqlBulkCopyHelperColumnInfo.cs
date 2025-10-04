namespace SqlBulkCopyHelper;

public class SqlBulkCopyHelperColumnInfo(string columnName, string quotedColumnName, string schemaDefinition)
{
    public string ColumnName { get; private set; } = columnName;
    public string QuotedColumnName { get; private set; } = quotedColumnName;
    public string SchemaDefinition { get; private set; } = schemaDefinition;

    public override string ToString() => SchemaDefinition;
}