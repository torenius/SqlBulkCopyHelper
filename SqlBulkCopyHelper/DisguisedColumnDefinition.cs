using System;

namespace SqlBulkCopyHelper;

internal class DisguisedColumnDefinition<TEntity>
{
    /// <summary>
    /// Name of the column. 
    /// </summary>
    public string ColumnName { get; set; } = null!;

    /// <summary>
    /// C# Type
    /// </summary>
    public Type Type { get; set; } = null!;

    /// <summary>
    /// Function that will return the column value for a specific entity.
    /// </summary>
    public Func<TEntity, object> PropertyGetter { get; set; } = null!;

    /// <summary>
    /// Can the column have NULL as a valid value?
    /// </summary>
    public bool Nullable { get; set; }
}
