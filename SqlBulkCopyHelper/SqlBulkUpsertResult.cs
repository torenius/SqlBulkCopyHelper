namespace SqlBulkCopyHelper;

/// <summary>
/// The number of rows that BulkUpsertAsync inserted, updated and left as they were.
/// </summary>
/// <param name="Inserted">Rows that didn't match a row in the table and were inserted</param>
/// <param name="Updated">Rows that matched a row in the table and updated it</param>
/// <param name="Unchanged">Rows that matched a row in the table but didn't update it, because of OnlyUpdateWhenChanged or because there are no columns to update</param>
/// <param name="DuplicatesRemoved">Rows that were skipped, since another row in the source had the same key. See OnDuplicateKey.</param>
public sealed record SqlBulkUpsertResult(long Inserted, long Updated, long Unchanged, long DuplicatesRemoved);
