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
    
    private TEntity? _currentEntity;
    private IEnumerator<TEntity>? _enumerator;
    private readonly Dictionary<string, int> _nameToIndex;

    public DisguisedDataReader(List<DisguisedColumnDefinition<TEntity>> columnDefinitions, IEnumerable<TEntity> entities)
    {
        _columnDefinitions = columnDefinitions;
        _enumerator = entities.GetEnumerator();
        
        _nameToIndex = _columnDefinitions
            .Select((x, i) => new { Name = x.ColumnName, Index = i })
            .ToDictionary(x => x.Name, x => x.Index);
    }

    public override bool GetBoolean(int ordinal) => (bool)GetValue(ordinal);

    public override byte GetByte(int ordinal) => (byte)GetValue(ordinal);

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        if (GetValue(ordinal) is not IEnumerable<byte> data) return 0;
        
        long bytesRead = 0;
        foreach (var x in data.Skip((int)dataOffset).Take(length).Select((b, i) => new { b, i }))
        {
            buffer[bufferOffset + x.i] = x.b;
            bytesRead++;
        }
        return bytesRead;
    }

    public override char GetChar(int ordinal) => (char)GetValue(ordinal);

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        if (GetValue(ordinal) is not IEnumerable<char> data) return 0;
        
        long charsRead = 0;
        foreach (var x in data.Skip((int)dataOffset).Take(length).Select((c, i) => new { c, i }))
        {
            buffer[bufferOffset + x.i] = x.c;
            charsRead++;
        }
        return charsRead;
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

    public override int GetOrdinal(string name) => _nameToIndex[name];

    public override string GetString(int ordinal) => (string)GetValue(ordinal);

    public override object GetValue(int ordinal) => _columnDefinitions[ordinal].PropertyGetter(_currentEntity!);

    public override int GetValues(object[] values)
    {
        var count = 0;
        for (var i = 0; i < _columnDefinitions.Count && i < values.Length; i++)
        {
            values[i] = this[i];
            count++;
        }

        return count;
    }

    public override bool IsDBNull(int ordinal)
    {
        var data = GetValue(ordinal);
        return data == DBNull.Value;
    }

    public override int FieldCount => _columnDefinitions.Count;

    public override object this[int ordinal] => GetValue(ordinal);

    public override object this[string name] => GetValue(GetOrdinal(name));

    public override int RecordsAffected => -1;
    public override bool HasRows { get; }
    public override bool IsClosed => _enumerator == null;

    public override bool NextResult() => false;

    public override bool Read()
    {
        if (IsClosed) throw new InvalidOperationException("The reader is closed.");
        
        var state = _enumerator!.MoveNext();
        _currentEntity = state ? _enumerator.Current : default(TEntity);
        return state;
    }

    public override int Depth => 0;

    public override IEnumerator GetEnumerator()
    {
        throw new NotImplementedException();
    }
    
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
        table.Columns.Add("NumericPrecision", typeof(int));
        table.Columns.Add("NumericScale", typeof(int));
        table.Columns.Add("AllowDBNull", typeof(bool));

        for (var i = 0; i < _columnDefinitions.Count; i++)
        {
            var row = table.NewRow();

            row["ColumnOrdinal"] = i;
            row["ColumnName"] = _columnDefinitions[i].ColumnName;
            row["DataType"] = _columnDefinitions[i].Type;
            row["DataTypeName"] = _columnDefinitions[i].Type.Name;
            row["ColumnSize"] = _columnDefinitions[i].ColumnSize;
            row["NumericPrecision"] = _columnDefinitions[i].NumericPrecision;
            row["NumericScale"] = _columnDefinitions[i].NumericScale;
            row["AllowDBNull"] = _columnDefinitions[i].Nullable;

            table.Rows.Add(row);
        }

        return table;
    }

    public override void Close()
    {
        if (_enumerator != null)
        {
            _enumerator.Dispose();
            _enumerator = null;
            _currentEntity = default(TEntity);
        }
    }
}