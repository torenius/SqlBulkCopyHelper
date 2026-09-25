using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SqlBulkCopyHelper;

/// <summary>
/// A DbDataReader over an IAsyncEnumerable. Rows are read with ReadAsync, the synchronous Read is not supported.
/// SqlBulkCopy.WriteToServerAsync uses ReadAsync, so the source is streamed without blocking threads.
/// </summary>
internal class AsyncDisguisedDataReader<TEntity> : DisguisedDataReaderBase<TEntity>
{
    private IAsyncEnumerator<TEntity>? _enumerator;
    private bool? _hasRows;

    /// <param name="columnDefinitions">The mapped columns</param>
    /// <param name="entities">The source</param>
    /// <param name="cancellationToken">Passed to the source when it's enumerated. IAsyncEnumerable gets the token once, not per MoveNextAsync.</param>
    public AsyncDisguisedDataReader(List<DisguisedColumnDefinition<TEntity>> columnDefinitions, IAsyncEnumerable<TEntity> entities, CancellationToken cancellationToken)
        : base(columnDefinitions)
    {
        _enumerator = entities.GetAsyncEnumerator(cancellationToken);
    }

    protected override TEntity Current => _enumerator!.Current;

    /// <summary>
    /// A property can't be async, so it's only known after the first ReadAsync.
    /// </summary>
    public override bool HasRows => _hasRows
        ?? (IsClosed ? false : throw new NotSupportedException("HasRows is only known after the first call to ReadAsync, when the source is an IAsyncEnumerable."));

    public override bool IsClosed => _enumerator == null;

    public override bool Read() =>
        throw new NotSupportedException("The source is an IAsyncEnumerable, use ReadAsync instead of Read.");

    public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsClosed) throw new InvalidOperationException("The reader is closed.");

        var state = await _enumerator!.MoveNextAsync().ConfigureAwait(false);
        _hasRows ??= state;

        SetCurrentRow(state);
        return state;
    }

    public override IEnumerator GetEnumerator() =>
        throw new NotSupportedException("The source is an IAsyncEnumerable, enumerating the reader synchronously is not supported.");

    public override async Task CloseAsync()
    {
        if (_enumerator is not null)
        {
            var enumerator = _enumerator;
            _enumerator = null;
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }

        SetCurrentRow(false);
    }

    public override async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Prefer CloseAsync/DisposeAsync. This blocks until the source is disposed,
    /// since not disposing it could leave for example a database connection open.
    /// </summary>
    public override void Close() => CloseAsync().GetAwaiter().GetResult();
}
