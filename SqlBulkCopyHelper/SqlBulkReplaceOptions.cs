using System;

namespace SqlBulkCopyHelper;

/// <summary>
/// What BulkReplaceAsync does when it has waited MaxDuration at low priority for the lock on the table. Same as ABORT_AFTER_WAIT in SQL Server.
/// </summary>
public enum AbortAfterWait
{
    /// <summary>
    /// Gives up and throws. Nothing is changed. ABORT_AFTER_WAIT = SELF
    /// </summary>
    Self,

    /// <summary>
    /// Continues to wait, at normal priority, so new readers are blocked behind the swap. ABORT_AFTER_WAIT = NONE
    /// </summary>
    None,

    /// <summary>
    /// Kills the transactions that block the swap. Requires ALTER ANY CONNECTION. ABORT_AFTER_WAIT = BLOCKERS
    /// </summary>
    Blockers
}

/// <summary>
/// Configures how BulkReplaceAsync swaps in the new rows.
/// </summary>
public sealed class SqlBulkReplaceOptions
{
    internal int? LowPriorityMaxDurationMinutes { get; private set; }
    internal AbortAfterWait AbortAfterWait { get; private set; }

    /// <summary>
    /// Waits for the lock on the table at low priority, so readers that come while the swap is waiting are not blocked behind it.
    /// Without it the swap waits at normal priority, and new readers queue behind it until the readers that block it are done.
    /// </summary>
    /// <param name="maxDurationMinutes">How many minutes to wait at low priority, at least 1</param>
    /// <param name="abortAfterWait">What to do after maxDurationMinutes. Default is to give up and throw.</param>
    /// <returns>The options so you can continue with the builder pattern</returns>
    public SqlBulkReplaceOptions WaitAtLowPriority(int maxDurationMinutes, AbortAfterWait abortAfterWait = AbortAfterWait.Self)
    {
        if (maxDurationMinutes < 1) throw new ArgumentOutOfRangeException(nameof(maxDurationMinutes), "The wait must be at least 1 minute.");
        if (!Enum.IsDefined(abortAfterWait)) throw new ArgumentOutOfRangeException(nameof(abortAfterWait));

        LowPriorityMaxDurationMinutes = maxDurationMinutes;
        AbortAfterWait = abortAfterWait;
        return this;
    }
}
