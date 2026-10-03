using System;
using System.Collections.Generic;
using System.Linq;

namespace SqlBulkCopyHelper;

/// <summary>
/// How BulkUpsertAsync handles rows in the source that have the same key.
/// </summary>
public enum DuplicateKeyHandling
{
    /// <summary>
    /// Throws an InvalidOperationException that tells which keys are duplicated. Nothing is inserted or updated.
    /// </summary>
    Throw,

    /// <summary>
    /// Keeps the first row with the key in the source and skips the rest.
    /// </summary>
    KeepFirst,

    /// <summary>
    /// Keeps the last row with the key in the source and skips the rest.
    /// </summary>
    KeepLast
}

/// <summary>
/// Configures how BulkUpsertAsync matches the source rows with the rows in the table, and what is updated.
/// </summary>
public sealed class SqlBulkUpsertOptions
{
    internal List<string> MatchOnColumns { get; private set; } = [];
    internal List<string> IgnoreOnUpdateColumns { get; private set; } = [];
    internal bool UpdateOnlyWhenChanged { get; private set; }
    internal DuplicateKeyHandling DuplicateKeyHandling { get; private set; } = DuplicateKeyHandling.Throw;

    /// <summary>
    /// The columns that identify a row, for example "Id" or "TenantId", "Sku". Required.
    /// A source row that matches a row in the table updates it, otherwise it's inserted.
    /// The columns must be mapped. Calling it again replaces the previous columns.
    /// NULL never matches, so a row with NULL in a key column is always inserted.
    /// </summary>
    /// <param name="columnNames">Mapped column names</param>
    /// <returns>The options so you can continue with the builder pattern</returns>
    public SqlBulkUpsertOptions MatchOn(params string[] columnNames)
    {
        MatchOnColumns = Validate(columnNames, nameof(columnNames));
        return this;
    }

    /// <summary>
    /// Columns that are inserted but never updated, for example "CreatedAt".
    /// The columns must be mapped. Calling it again replaces the previous columns.
    /// </summary>
    /// <param name="columnNames">Mapped column names</param>
    /// <returns>The options so you can continue with the builder pattern</returns>
    public SqlBulkUpsertOptions IgnoreOnUpdate(params string[] columnNames)
    {
        IgnoreOnUpdateColumns = Validate(columnNames, nameof(columnNames));
        return this;
    }

    /// <summary>
    /// Only updates rows where at least one of the updated columns has a different value. NULL is considered equal to NULL.
    /// Rows that are not updated are counted as Unchanged, and triggers don't fire for them.
    /// </summary>
    /// <returns>The options so you can continue with the builder pattern</returns>
    public SqlBulkUpsertOptions OnlyUpdateWhenChanged()
    {
        UpdateOnlyWhenChanged = true;
        return this;
    }

    /// <summary>
    /// What to do when the source has more than one row with the same key. Default is DuplicateKeyHandling.Throw.
    /// </summary>
    /// <param name="handling">How duplicates are handled</param>
    /// <returns>The options so you can continue with the builder pattern</returns>
    public SqlBulkUpsertOptions OnDuplicateKey(DuplicateKeyHandling handling)
    {
        if (!Enum.IsDefined(handling)) throw new ArgumentOutOfRangeException(nameof(handling));
        DuplicateKeyHandling = handling;
        return this;
    }

    private static List<string> Validate(string[] columnNames, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(columnNames, parameterName);
        if (columnNames.Length == 0) throw new ArgumentException("At least one column is required", parameterName);
        if (columnNames.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Column names can't be null or empty", parameterName);

        return columnNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
