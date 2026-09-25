using Shouldly;

namespace SqlBulkCopyHelper.Tests;

public class DisguisedDataReaderTests
{
    [Fact]
    public void ReaderTest()
    {
        var helper = new SqlBulkCopyHelper<Tuple<int, int?>>("Test")
            .Map("IntColumn", x => x.Item1)
            .Map("NullableIntColumn", x => x.Item2);
        
        const int nrOrRows = 15;
        var testData = TestDataFactory.GetTestData(nrOrRows)
            .Select(x => new Tuple<int, int?>(x.IntColumn, x.NullableIntColumn))
            .ToList();

        var result = new List<Tuple<int, int?>>();
        var reader = helper.GetDataReader(testData);
        while (reader.Read())
        {
            var item1 = reader.GetInt32(0);
            int? item2 = null;
            
            if (!reader.IsDBNull(1))
            {
                item2 = reader.GetInt32(1);
            }
            
            var tuple = new Tuple<int, int?>(item1, item2);
            result.Add(tuple);
        }
        
        result.ShouldBeEquivalentTo(testData);
    }

    private class Row
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public byte[]? Data { get; set; }
    }

    private static readonly List<Row> Rows =
    [
        new() { Id = 1, Name = "Hello world", Data = [1, 2, 3, 4, 5] },
        new() { Id = 2, Name = null, Data = null }
    ];

    private static SqlBulkCopyHelper<Row> CreateHelper() => new SqlBulkCopyHelper<Row>("Test")
        .Map("Id", x => x.Id)
        .Map("Name", x => x.Name)
        .Map("Data", x => x.Data);

    [Fact]
    public void Reader_NullIsDBNull()
    {
        using var reader = CreateHelper().GetDataReader(Rows.Skip(1));
        reader.Read().ShouldBeTrue();

        reader.GetValue(1).ShouldBe(DBNull.Value);
        reader.IsDBNull(1).ShouldBeTrue();

        var values = new object[3];
        reader.GetValues(values).ShouldBe(3);
        values[1].ShouldBe(DBNull.Value);
        values[2].ShouldBe(DBNull.Value);
    }

    [Fact]
    public void Reader_GetBytes()
    {
        using var reader = CreateHelper().GetDataReader(Rows);
        reader.Read().ShouldBeTrue();

        reader.GetBytes(2, 0, null, 0, 0).ShouldBe(5);

        var buffer = new byte[5];
        reader.GetBytes(2, 0, buffer, 0, 3).ShouldBe(3);
        reader.GetBytes(2, 3, buffer, 3, 3).ShouldBe(2); // Only 2 bytes left
        reader.GetBytes(2, 5, buffer, 0, 3).ShouldBe(0);
        buffer.ShouldBe(new byte[] { 1, 2, 3, 4, 5 });
    }

    [Fact]
    public void Reader_GetChars()
    {
        using var reader = CreateHelper().GetDataReader(Rows);
        reader.Read().ShouldBeTrue();

        reader.GetChars(1, 0, null, 0, 0).ShouldBe(11);

        var buffer = new char[5];
        reader.GetChars(1, 6, buffer, 0, 5).ShouldBe(5);
        new string(buffer).ShouldBe("world");
    }

    [Fact]
    public void Reader_HasRows()
    {
        using (var empty = CreateHelper().GetDataReader([]))
        {
            empty.HasRows.ShouldBeFalse();
            empty.Read().ShouldBeFalse();
        }

        using var reader = CreateHelper().GetDataReader(Rows);
        reader.HasRows.ShouldBeTrue();

        // HasRows must not skip the first row
        reader.Read().ShouldBeTrue();
        reader.GetInt32(0).ShouldBe(1);
        reader.Read().ShouldBeTrue();
        reader.GetInt32(0).ShouldBe(2);
        reader.Read().ShouldBeFalse();
        reader.HasRows.ShouldBeTrue();
    }

    [Fact]
    public void Reader_GetOrdinal()
    {
        using var reader = CreateHelper().GetDataReader(Rows);

        reader.GetOrdinal("Name").ShouldBe(1);
        reader.GetOrdinal("name").ShouldBe(1);
        Should.Throw<IndexOutOfRangeException>(() => reader.GetOrdinal("Missing"));
    }

    [Fact]
    public void Reader_GetEnumerator()
    {
        using var reader = CreateHelper().GetDataReader(Rows);

        var ids = reader.Cast<System.Data.IDataRecord>().Select(x => x.GetInt32(0)).ToList();

        ids.ShouldBe([1, 2]);
    }

    [Fact]
    public void Reader_PropertyGetterIsCalledOncePerRow()
    {
        var calls = 0;
        var helper = new SqlBulkCopyHelper<Row>("Test")
            .Map("Name", x =>
            {
                calls++;
                return x.Name;
            });

        using var reader = helper.GetDataReader(Rows);
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
            {
                reader.GetValue(0);
                reader.GetString(0);
            }
        }

        calls.ShouldBe(Rows.Count);
    }

    [Fact]
    public void Reader_PropertyGetterIsOnlyCalledForReadColumns()
    {
        var idCalls = 0;
        var nameCalls = 0;
        var helper = new SqlBulkCopyHelper<Row>("Test")
            .Map("Id", x =>
            {
                idCalls++;
                return x.Id;
            })
            .Map("Name", x =>
            {
                nameCalls++;
                return x.Name;
            });

        using var reader = helper.GetDataReader(Rows);
        var ids = new List<int>();
        while (reader.Read())
        {
            ids.Add(reader.GetInt32(0));
        }

        ids.ShouldBe([1, 2]);
        idCalls.ShouldBe(Rows.Count);
        nameCalls.ShouldBe(0);
    }

    [Fact]
    public void Reader_GetValueBeforeRead_Throws()
    {
        using var reader = CreateHelper().GetDataReader(Rows);

        Should.Throw<InvalidOperationException>(() => reader.GetValue(0));
    }
}