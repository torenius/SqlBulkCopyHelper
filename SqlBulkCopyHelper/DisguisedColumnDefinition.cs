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
    /// Value used for nvarchar, varchar and varbinary
    /// -1 = max
    /// nvarchar have a maximum value of 4000
    /// varchar/varbinary have a maximum value of 8000
    /// </summary>
    public int ColumnSize { get; set; } = -1;

    /// <summary>
    /// Precision for decimal and numeric.
    /// Max value is 38. Default is 18.
    /// Represents the total number of digits (right and left of the decimal point).
    /// </summary>
    public int NumericPrecision { get; set; } = 18;

    /// <summary>
    /// Scale of decimal and numeric.
    /// Max value is 38 - NumericPrecision. Default is 0.
    /// Represent max numbers to the right of the decimal point.
    /// </summary>
    public int NumericScale { get; set; } = 0;

    /// <summary>
    /// Can the column have NULL as a valid value?
    /// </summary>
    public bool Nullable { get; set; } = false;
}
