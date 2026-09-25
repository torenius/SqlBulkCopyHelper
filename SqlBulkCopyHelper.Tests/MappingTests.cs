using System.Reflection;
using Shouldly;

namespace SqlBulkCopyHelper.Tests;

public class MappingTests
{
    private class Test
    {
        private string _writeOnly = "";

        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string PrivateGetter { private get; set; } = "";
        public string WriteOnly { set => _writeOnly = value; }
        public static int StaticProperty { get; set; }
        public int this[int index] => index;
    }

    [Fact]
    public void MapAllPublicProperties_SkipsPropertiesThatCantBeMapped()
    {
        var columns = new SqlBulkCopyHelper<Test>("Test")
            .MapAllPublicProperties()
            .GetColumnInfo()
            .Select(x => x.ColumnName)
            .ToList();

        columns.ShouldBe(["Id", "Name"]);
    }

    [Fact]
    public void MapAllPublicProperties_SimpleValues_Throws()
    {
        Should.Throw<InvalidOperationException>(() => new SqlBulkCopyHelper<string>("Test").MapAllPublicProperties())
            .Message.ShouldContain(".Map(\"ColumnName\")");
        Should.Throw<InvalidOperationException>(() => new SqlBulkCopyHelper<byte[]>("Test").MapAllPublicProperties());
        Should.Throw<InvalidOperationException>(() => new SqlBulkCopyHelper<char[]>("Test").MapAllPublicProperties());
    }

    [Fact]
    public void SqlConnectionExtension_ListOfStringsWithoutColumnName_Throws()
    {
        using var connection = new Microsoft.Data.SqlClient.SqlConnection();
        List<string> values = ["A", "B"];

        // Used to insert string.Length into a "Length" column
        Should.Throw<InvalidOperationException>(() =>
        {
            _ = connection.BulkInsertAsync("#Test", values);
        });
    }

    [Theory]
    [InlineData("PrivateGetter")]
    [InlineData("WriteOnly")]
    [InlineData("StaticProperty")]
    [InlineData("Item")] // The indexer
    public void MapProperty_ThrowsForPropertiesThatCantBeMapped(string propertyName)
    {
        var property = typeof(Test).GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)!;
        var helper = new SqlBulkCopyHelper<Test>("Test");

        Should.Throw<ArgumentException>(() => helper.MapProperty(property));
    }

    [Fact]
    public void MapProperties_ValidatesAllPropertiesBeforeMapping()
    {
        var properties = new[] { typeof(Test).GetProperty("Id")!, typeof(Test).GetProperty("WriteOnly")! };
        var helper = new SqlBulkCopyHelper<Test>("Test");

        Should.Throw<ArgumentException>(() => helper.MapProperties(properties));

        helper.GetColumnInfo().ShouldBeEmpty();
    }

    [Fact]
    public void Map_SameColumnNameWithDifferentCase_ReplacesMapping()
    {
        var helper = new SqlBulkCopyHelper<Test>("Test")
            .Map("Id", x => x.Id)
            .Map("id", x => x.Id * 2);

        var column = helper.GetColumnInfo().ShouldHaveSingleItem();
        column.ColumnName.ShouldBe("id");

        using var reader = helper.GetDataReader([new Test { Id = 5 }]);
        reader.Read().ShouldBeTrue();
        reader.GetInt32(reader.GetOrdinal("ID")).ShouldBe(10);
    }

    [Fact]
    public void RemoveMap_IsCaseInsensitive()
    {
        var helper = new SqlBulkCopyHelper<Test>("Test")
            .Map("Id", x => x.Id)
            .Map("Name", x => x.Name)
            .RemoveMap("NAME");

        helper.GetColumnInfo().Select(x => x.ColumnName).ShouldBe(["Id"]);
    }

    [Fact]
    public void GetDataReader_IsNotAffectedByLaterMappings()
    {
        var helper = new SqlBulkCopyHelper<Test>("Test")
            .Map("Id", x => x.Id);

        using var reader = helper.GetDataReader([new Test { Id = 1, Name = "A" }]);
        helper.Map("Name", x => x.Name);

        reader.FieldCount.ShouldBe(1);
        reader.Read().ShouldBeTrue();
        reader.GetInt32(0).ShouldBe(1);
    }
}
