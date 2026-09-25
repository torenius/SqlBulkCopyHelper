using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;

namespace SqlBulkCopyHelper;

internal class DisguisedDataReader<TEntity> : DbDataReader
{
    private readonly List<DisguisedColumnDefinition<TEntity>> _columnDefinitions;
    private readonly Dictionary<string, int> _nameToIndex;

    // Cached values for the current row. A PropertyGetter is only called when the column is read, and at most once per row.
    private readonly object[] _values;
    private readonly long[] _valueRow; // Which row the cached value belongs to
    private long _currentRow; // 0 = no row has been read yet
    private bool _hasCurrentRow;

    private IEnumerator<TEntity>? _enumerator;

    // HasRows needs to look at the first row before Read is called. The result is saved and used by the first Read.
    private bool? _hasRows;
    private bool _peekedFirstRow;

    public DisguisedDataReader(List<DisguisedColumnDefinition<TEntity>> columnDefinitions, IEnumerable<TEntity> entities)
    {
        // A copy, so changes to the helper's mapping don't affect a reader that has already been created
        _columnDefinitions = [.. columnDefinitions];
        _enumerator = entities.GetEnumerator();
        _values = new object[_columnDefinitions.Count];
        _valueRow = new long[_columnDefinitions.Count];

        // Column names are unique case-insensitively, see SqlBulkCopyHelper.RemoveMap
        _nameToIndex = _columnDefinitions
            .Select((x, i) => new { Name = x.ColumnName, Index = i })
            .ToDictionary(x => x.Name, x => x.Index, StringComparer.OrdinalIgnoreCase);
    }

    public override bool GetBoolean(int ordinal) => (bool)GetValue(ordinal);

    public override byte GetByte(int ordinal) => (byte)GetValue(ordinal);

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        ReadOnlySpan<byte> data = GetValue(ordinal) switch
        {
            byte[] bytes => bytes,
            IEnumerable<byte> bytes => bytes.ToArray(),
            _ => throw new InvalidCastException($"Ordinal {ordinal} is not a byte array!")
        };

        return CopyTo(data, dataOffset, buffer, bufferOffset, length);
    }

    public override char GetChar(int ordinal) => (char)GetValue(ordinal);

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        ReadOnlySpan<char> data = GetValue(ordinal) switch
        {
            string chars => chars,
            char[] chars => chars,
            IEnumerable<char> chars => chars.ToArray(),
            _ => throw new InvalidCastException($"Ordinal {ordinal} is not a char array!")
        };

        return CopyTo(data, dataOffset, buffer, bufferOffset, length);
    }

    /// <summary>
    /// Follows the DbDataReader contract for GetBytes and GetChars. If buffer is null the total length is returned.
    /// </summary>
    private static long CopyTo<T>(ReadOnlySpan<T> data, long dataOffset, T[]? buffer, int bufferOffset, int length)
    {
        if (buffer is null)
        {
            return data.Length;
        }

        if (dataOffset >= data.Length)
        {
            return 0;
        }

        var count = (int)Math.Min(length, data.Length - dataOffset);
        data.Slice((int)dataOffset, count).CopyTo(buffer.AsSpan(bufferOffset));
        return count;
    }

    public override string GetDataTypeName(int ordinal) => _columnDefinitions[ordinal].Type.Name;

    public override DateTime GetDateTime(int ordinal)  => (DateTime)GetValue(ordinal);

    public override decimal GetDecimal(int ordinal)  => (decimal)GetValue(ordinal);

    public override double GetDouble(int ordinal) => (double)GetValue(ordinal);

    public override Type GetFieldType(int ordinal) => _columnDefinitions[ordinal].Type;

    public override float GetFloat(int ordinal)  => (float)GetValue(ordinal);

    public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);

    public override short GetInt16(int ordinal) => (short)GetValue(ordinal);

    public override int GetInt32(int ordinal) => (int)GetValue(ordinal);

    public override long GetInt64(int ordinal) => (long)GetValue(ordinal);

    public override string GetName(int ordinal) => _columnDefinitions[ordinal].ColumnName;

    public override int GetOrdinal(string name)
    {
        return _nameToIndex.TryGetValue(name, out var index)
            ? index
            : throw new IndexOutOfRangeException($"Column '{name}' does not exist.");
    }

    public override string GetString(int ordinal) => (string)GetValue(ordinal);

    public override object GetValue(int ordinal)
    {
        if (!_hasCurrentRow) throw new InvalidOperationException("No data exists for the row. Call Read first.");

        if (_valueRow[ordinal] != _currentRow)
        {
            _values[ordinal] = _columnDefinitions[ordinal].PropertyGetter(_enumerator!.Current) ?? DBNull.Value;
            _valueRow[ordinal] = _currentRow;
        }

        return _values[ordinal];
    }

    public override int GetValues(object[] values)
    {
        var count = Math.Min(_values.Length, values.Length);
        for (var i = 0; i < count; i++)
        {
            values[i] = GetValue(i);
        }

        return count;
    }

    public override bool IsDBNull(int ordinal) => GetValue(ordinal) is DBNull;

    public override int FieldCount => _columnDefinitions.Count;

    public override object this[int ordinal] => GetValue(ordinal);

    public override object this[string name] => GetValue(GetOrdinal(name));

    public override int RecordsAffected => -1;

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

    public override bool NextResult() => false;

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

        _hasCurrentRow = state;
        if (state)
        {
            // Invalidates all cached values, they will be fetched when they are read
            _currentRow++;
        }
        else
        {
            Array.Clear(_values);
        }

        return state;
    }

    public override int Depth => 0;

    public override IEnumerator GetEnumerator() => new DbEnumerator(this);

    /// <summary>
    /// Used by DataTable.Load method
    /// </summary>
    /// <returns>A DataTable containing rows with metadata info for the columns in this reader</returns>
    public override DataTable GetSchemaTable()
    {
        var table = new DataTable("SchemaTable");
        table.Columns.Add("ColumnOrdinal", typeof(int));
        table.Columns.Add("ColumnName", typeof(string));
        table.Columns.Add("DataType", typeof(Type));
        table.Columns.Add("DataTypeName", typeof(string));
        table.Columns.Add("ColumnSize", typeof(int));
        table.Columns.Add("AllowDBNull", typeof(bool));

        for (var i = 0; i < _columnDefinitions.Count; i++)
        {
            var row = table.NewRow();

            row["ColumnOrdinal"] = i;
            row["ColumnName"] = _columnDefinitions[i].ColumnName;
            row["DataType"] = _columnDefinitions[i].Type;
            row["DataTypeName"] = _columnDefinitions[i].Type.Name;
            row["ColumnSize"] = -1;
            row["AllowDBNull"] = _columnDefinitions[i].Nullable;

            table.Rows.Add(row);
        }

        return table;
    }

    public override void Close()
    {
        _enumerator?.Dispose();
        _enumerator = null;
        _hasCurrentRow = false;
        Array.Clear(_values);
    }
}
