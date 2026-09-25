using System.Data.SqlTypes;
using Shouldly;

namespace SqlBulkCopyHelper.Tests;

public class TableNameTests
{
    private static string CreateTableLine(string tableName, bool useQuoting)
    {
        var helper = new SqlBulkCopyHelper<int>(tableName).Map("Id");
        if (useQuoting)
        {
            helper.UseBracketQuoting();
        }

        return helper.CreateTableScript().Split(Environment.NewLine)[0];
    }

    [Theory]
    [InlineData("Test", "[Test]")]
    [InlineData("dbo.Test", "[dbo].[Test]")]
    [InlineData("[dbo].[Test]", "[dbo].[Test]")]
    [InlineData("[dbo].[My.Table]", "[dbo].[My.Table]")]
    [InlineData("dbo.[My.Table]", "[dbo].[My.Table]")]
    [InlineData("[My]]Table]", "[My]]Table]")]
    [InlineData("\"dbo\".\"My.Table\"", "[dbo].[My.Table]")]
    [InlineData("db..Test", "[db]..[Test]")]
    [InlineData("#Test", "[#Test]")]
    public void TableName_WithQuoting(string tableName, string expected)
    {
        CreateTableLine(tableName, useQuoting: true).ShouldBe("CREATE TABLE " + expected);
    }

    [Theory]
    [InlineData("dbo.Test")]
    [InlineData("[dbo].[My.Table]")]
    [InlineData("#Test")]
    public void TableName_WithoutQuoting_IsUsedAsProvided(string tableName)
    {
        CreateTableLine(tableName, useQuoting: false).ShouldBe("CREATE TABLE " + tableName);
    }

    [Fact]
    public void TableName_NotTerminatedQuote_Throws()
    {
        Should.Throw<ArgumentException>(() => new SqlBulkCopyHelper<int>("[dbo.Test"));
    }

    [Theory]
    [InlineData("#Test", "IF OBJECT_ID('tempdb..#Test') IS NULL")]
    [InlineData("[#Test]", "IF OBJECT_ID('tempdb..[#Test]') IS NULL")]
    [InlineData("dbo.[#NotTemp]", "IF OBJECT_ID('dbo.[#NotTemp]') IS NULL")]
    [InlineData("[O'Brien]", "IF OBJECT_ID('[O''Brien]') IS NULL")]
    public void CreateTableScript_CheckIfTableExists(string tableName, string expected)
    {
        var script = new SqlBulkCopyHelper<int>(tableName)
            .Map("Id")
            .CreateTableScript(checkIfTableExists: true);

        script.Split(Environment.NewLine)[0].ShouldBe(expected);
    }

    [Fact]
    public void CreateTableScript_UnknownType_Throws()
    {
        var helper = new SqlBulkCopyHelper<int>("Test")
            .Map("Value", x => new List<int> { x });

        var exception = Should.Throw<InvalidOperationException>(() => helper.CreateTableScript());
        exception.Message.ShouldContain("Value");
        exception.Message.ShouldContain("SchemaDefinitionMapping");
    }

    [Fact]
    public void CreateTableScript_UnknownType_CanBeAddedToMapping()
    {
        var helper = new SqlBulkCopyHelper<int>("Test")
            .Map("Value", x => new SqlInt32(x));

        helper.SchemaDefinitionMapping[typeof(SqlInt32)] = "int";

        helper.GetColumnInfo().ShouldHaveSingleItem().SchemaDefinition.ShouldBe("Value int null");
    }
}
