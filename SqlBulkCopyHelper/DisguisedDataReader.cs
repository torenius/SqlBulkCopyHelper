using System;
using System.Collections;
using System.Collections.Generic;
using System.Data.Common;

namespace SqlBulkCopyHelper;

internal class DisguisedDataReader<TEntity> : DisguisedDataReaderBase<TEntity>
{
    private IEnumerator<TEntity>? _enumerator;

    // HasRows needs to look at the first row before Read is called. The result is saved and used by the first Read.
    private bool? _hasRows;
    private bool _peekedFirstRow;

    public DisguisedDataReader(List<DisguisedColumnDefinition<TEntity>> columnDefinitions, IEnumerable<TEntity> entities)
        : base(columnDefinitions)
    {
        _enumerator = entities.GetEnumerator();
    }

    protected override TEntity Current => _enumerator!.Current;

    public override bool HasRows
    {
        get
        {
            if (_hasRows is null && !IsClosed)
            {
                _hasRows = _enumerator!.MoveNext();
                _peekedFirstRow = true;
            }

            return _hasRows ?? false;
        }
    }

    public override bool IsClosed => _enumerator == null;

    public override bool Read()
    {
        if (IsClosed) throw new InvalidOperationException("The reader is closed.");

        bool state;
        if (_peekedFirstRow)
        {
            _peekedFirstRow = false;
            state = _hasRows!.Value;
        }
        else
        {
            state = _enumerator!.MoveNext();
            _hasRows ??= state;
        }

        SetCurrentRow(state);
        return state;
    }

    public override IEnumerator GetEnumerator() => new DbEnumerator(this);

    public override void Close()
    {
        _enumerator?.Dispose();
        _enumerator = null;
        SetCurrentRow(false);
    }
}
