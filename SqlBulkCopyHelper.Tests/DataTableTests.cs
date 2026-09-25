using Shouldly;

namespace SqlBulkCopyHelper.Tests;

public class DataTableTests
{
    private class Test
    {
        public int Id { get; set; }
        public required string Name { get; set; }
    }
    
    [Fact]
    public void DataTable_Simple()
    {
        var helper = new SqlBulkCopyHelper<Test>("Test")
            .Map("Id", x => x.Id)
            .Map("Name", x => x.Name);

        var rows = new List<Test>
        {
            new Test
            {
                Id = 1,
                Name = "A"
            },
            new Test
            {
                Id = 2,
                Name = "B"
            }
        };
        
        var dt = helper.GetDataTable(rows);
        
        dt.Columns.Count.ShouldBe(2);
        dt.Columns[0].ColumnName.ShouldBe("Id");
        dt.Columns[0].DataType.ShouldBe(typeof(int));
        dt.Columns[1].ColumnName.ShouldBe("Name");
        dt.Columns[1].DataType.ShouldBe(typeof(string));
        
        dt.Rows.Count.ShouldBe(2);
        dt.Rows[0]["Id"].ShouldBe(1);
        dt.Rows[0]["Name"].ShouldBe("A");
        dt.Rows[1]["Id"].ShouldBe(2);
        dt.Rows[1]["Name"].ShouldBe("B");
    }
    
    [Fact]
    public void DataTable_Dictionary()
    {
        var helper = new SqlBulkCopyHelper<Dictionary<string, object>>("Test")
            .Map("Id", x => x["Id"], typeof(int))
            .Map("Name", x => x["Name"], typeof(string));

        var rows = new List<Dictionary<string, object>>();
        var a = new Dictionary<string, object>
        {
            { "Id", 1 },
            { "Name", "A" }
        };
        rows.Add(a);
        
        var b = new Dictionary<string, object>
        {
            { "Id", 2 },
            { "Name", "B" }
        };
        rows.Add(b);
        
        var dt = helper.GetDataTable(rows);
        
        dt.Columns.Count.ShouldBe(2);
        dt.Columns[0].ColumnName.ShouldBe("Id");
        dt.Columns[0].DataType.ShouldBe(typeof(int));
        dt.Columns[1].ColumnName.ShouldBe("Name");
        dt.Columns[1].DataType.ShouldBe(typeof(string));
        
        dt.Rows.Count.ShouldBe(2);
        dt.Rows[0]["Id"].ShouldBe(1);
        dt.Rows[0]["Name"].ShouldBe("A");
        dt.Rows[1]["Id"].ShouldBe(2);
        dt.Rows[1]["Name"].ShouldBe("B");
    }

    [Fact]
    public void DataTable_MoreDataTypes()
    {
        var helper = new SqlBulkCopyHelper<TestData>("Test")
            .MapAllPublicProperties();

        const int nrOrRows = 15;
        var testData = TestDataFactory.GetTestData(nrOrRows).ToList();
        
        var dt = helper.GetDataTable(testData);
        
        dt.Columns.Count.ShouldBe(13);
        dt.Columns[0].ColumnName.ShouldBe("BoolColumn");
        dt.Columns[0].DataType.ShouldBe(typeof(bool));
        
        dt.Columns[1].ColumnName.ShouldBe("ByteColumn");
        dt.Columns[1].DataType.ShouldBe(typeof(byte));
        
        dt.Columns[2].ColumnName.ShouldBe("ByteArrayColumn");
        dt.Columns[2].DataType.ShouldBe(typeof(byte[]));
        
        dt.Columns[3].ColumnName.ShouldBe("ShortColumn");
        dt.Columns[3].DataType.ShouldBe(typeof(short));
        
        dt.Columns[4].ColumnName.ShouldBe("IntColumn");
        dt.Columns[4].DataType.ShouldBe(typeof(int));
        
        dt.Columns[5].ColumnName.ShouldBe("LongColumn");
        dt.Columns[5].DataType.ShouldBe(typeof(long));
        
        dt.Columns[6].ColumnName.ShouldBe("DecimalColumn");
        dt.Columns[6].DataType.ShouldBe(typeof(decimal));
        
        dt.Columns[7].ColumnName.ShouldBe("DoubleColumn");
        dt.Columns[7].DataType.ShouldBe(typeof(double));
        
        dt.Columns[8].ColumnName.ShouldBe("DateTimeColumn");
        dt.Columns[8].DataType.ShouldBe(typeof(DateTime));
        
        dt.Columns[9].ColumnName.ShouldBe("GuidColumn");
        dt.Columns[9].DataType.ShouldBe(typeof(Guid));
        
        dt.Columns[10].ColumnName.ShouldBe("StringColumn");
        dt.Columns[10].DataType.ShouldBe(typeof(string));
        
        dt.Columns[11].ColumnName.ShouldBe("NullableIntColumn");
        dt.Columns[11].DataType.ShouldBe(typeof(int));
        
        dt.Columns[12].ColumnName.ShouldBe("CharColumn");
        dt.Columns[12].DataType.ShouldBe(typeof(char));
        
        dt.Rows.Count.ShouldBe(nrOrRows);
        for (var i = 0; i < nrOrRows; i++)
        {
            var isNotNull = (bool)dt.Rows[i]["BoolColumn"];
            isNotNull.ShouldBe(testData[i].BoolColumn);
            dt.Rows[i]["ByteColumn"].ShouldBe(testData[i].ByteColumn);
            dt.Rows[i]["ByteArrayColumn"].ShouldBe(testData[i].ByteArrayColumn);
            dt.Rows[i]["ShortColumn"].ShouldBe(testData[i].ShortColumn);
            dt.Rows[i]["IntColumn"].ShouldBe(testData[i].IntColumn);
            dt.Rows[i]["LongColumn"].ShouldBe(testData[i].LongColumn);
            dt.Rows[i]["DecimalColumn"].ShouldBe(testData[i].DecimalColumn);
            dt.Rows[i]["DoubleColumn"].ShouldBe(testData[i].DoubleColumn);
            dt.Rows[i]["DateTimeColumn"].ShouldBe(testData[i].DateTimeColumn);
            dt.Rows[i]["GuidColumn"].ShouldBe(testData[i].GuidColumn);
            dt.Rows[i]["StringColumn"].ShouldBe(testData[i].StringColumn);

            if (isNotNull)
            {
                dt.Rows[i]["NullableIntColumn"].ShouldBe(testData[i].NullableIntColumn);
            }
            else
            {
                dt.Rows[i]["NullableIntColumn"].ShouldBe(DBNull.Value);
            }
            
            dt.Rows[i]["CharColumn"].ShouldBe(testData[i].CharColumn);
        }
    }

    [Fact]
    public void DataTable_ListOfInt()
    {
        var helper = new SqlBulkCopyHelper<int>("Test")
            .Map("IntColumn");
        
        const int nrOrRows = 15;
        var testData = Enumerable.Range(1, nrOrRows).ToList();
        
        var dt = helper.GetDataTable(testData);
        
        dt.Columns.Count.ShouldBe(1);
        dt.Columns[0].ColumnName.ShouldBe("IntColumn");
        dt.Columns[0].DataType.ShouldBe(typeof(int));
        
        dt.Rows.Count.ShouldBe(nrOrRows);
    }

    private class NullableTest
    {
        public int Id { get; set; }
        public required string Name { get; set; }
        public string? Description { get; set; }
        public byte[]? Data { get; set; }
    }

    [Fact]
    public void DataTable_NullReferenceTypes_Map()
    {
        var helper = new SqlBulkCopyHelper<NullableTest>("Test")
            .Map("Id", x => x.Id)
            .Map("Description", x => x.Description)
            .Map("Data", x => x.Data);

        var rows = new List<NullableTest>
        {
            new() { Id = 1, Name = "A", Description = null, Data = null },
            new() { Id = 2, Name = "B", Description = "Desc", Data = [1, 2] }
        };

        var dt = helper.GetDataTable(rows);

        dt.Rows.Count.ShouldBe(2);
        dt.Rows[0]["Description"].ShouldBe(DBNull.Value);
        dt.Rows[0]["Data"].ShouldBe(DBNull.Value);
        dt.Rows[1]["Description"].ShouldBe("Desc");
    }

    [Fact]
    public void DataTable_NullReferenceTypes_MapAllPublicProperties()
    {
        var helper = new SqlBulkCopyHelper<NullableTest>("Test")
            .MapAllPublicProperties();

        var rows = new List<NullableTest>
        {
            new() { Id = 1, Name = "A", Description = null, Data = null }
        };

        var dt = helper.GetDataTable(rows);

        dt.Rows.Count.ShouldBe(1);
        dt.Rows[0]["Description"].ShouldBe(DBNull.Value);
        dt.Rows[0]["Data"].ShouldBe(DBNull.Value);
    }

    [Fact]
    public void CreateTableScript_RespectsNullableReferenceTypes()
    {
        var columns = new SqlBulkCopyHelper<NullableTest>("Test")
            .MapAllPublicProperties()
            .GetColumnInfo(columnsAreAlwaysNullable: false)
            .ToDictionary(x => x.ColumnName, x => x.SchemaDefinition);

        columns["Id"].ShouldBe("Id int not null");
        columns["Name"].ShouldBe("Name nvarchar(max) not null");
        columns["Description"].ShouldBe("Description nvarchar(max) null");
        columns["Data"].ShouldBe("Data varbinary(max) null");
    }

    private class MappingTest
    {
        public ulong ULong { get; set; }
        public DateOnly DateOnly { get; set; }
        public TimeOnly TimeOnly { get; set; }
        public decimal Decimal { get; set; }
        public decimal? NullableDecimal { get; set; }
    }

    [Fact]
    public void SchemaDefinitionMapping_NullableTypesUseUnderlyingType()
    {
        var helper = new SqlBulkCopyHelper<MappingTest>("Test")
            .MapAllPublicProperties();

        helper.SchemaDefinitionMapping[typeof(decimal)] = "numeric(18,2)";

        var columns = helper
            .GetColumnInfo(columnsAreAlwaysNullable: false)
            .ToDictionary(x => x.ColumnName, x => x.SchemaDefinition);

        columns["ULong"].ShouldBe("ULong numeric(20,0) not null");
        columns["DateOnly"].ShouldBe("DateOnly date not null");
        columns["TimeOnly"].ShouldBe("TimeOnly time(7) not null");
        columns["Decimal"].ShouldBe("Decimal numeric(18,2) not null");
        columns["NullableDecimal"].ShouldBe("NullableDecimal numeric(18,2) null");
    }
}