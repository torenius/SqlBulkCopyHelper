namespace SqlBulkCopyHelper;

/// <summary>
/// Information about a mapped column.
/// </summary>
/// <param name="columnName">Name of the mapped column</param>
/// <param name="quotedColumnName">Quoted name of the column, or the same as columnName if quoting is not used</param>
/// <param name="databaseColumnType">The database type of the column</param>
/// <param name="nullable">True if the column is nullable</param>
/// <param name="schemaDefinition">A simple create table definition of the column</param>
public class SqlBulkCopyHelperColumnInfo(string columnName, string quotedColumnName, string databaseColumnType, bool nullable, string schemaDefinition)
{
    /// <summary>
    /// Name of a mapped column.
    /// </summary>
    public string ColumnName { get; private set; } = columnName;

    /// <summary>
    /// If quoting is used, it's the quoted value of ColumnName.
    /// If quoting is not used, it's the same value as ColumnName.
    /// </summary>
    public string QuotedColumnName { get; private set; } = quotedColumnName;

    /// <summary>
    /// What database type this column is considered to have.
    /// </summary>
    public string DatabaseColumnType { get; private set; } = databaseColumnType;

    /// <summary>
    /// True if the type that was mapped are nullable, else false. 
    /// </summary>
    public bool Nullable { get; private set; } = nullable;
    
    /// <summary>
    /// A simple create table definition of the column.
    /// Example: Id int not null
    /// </summary>
    public string SchemaDefinition { get; private set; } = schemaDefinition;

    /// <summary>
    /// Returns the SchemaDefinition.
    /// </summary>
    public override string ToString() => SchemaDefinition;
}