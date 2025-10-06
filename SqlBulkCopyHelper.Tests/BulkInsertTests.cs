using Dapper;
using Microsoft.Data.SqlClient;
using Shouldly;

namespace SqlBulkCopyHelper.Tests;

public class BulkInsertTests(MsSqlFixture fixture) : IClassFixture<MsSqlFixture>
{
    private readonly string _connectionString = fixture.Container.GetConnectionString();
    
    [Fact]
    public async Task BulkInsert_DifferentDataTypes()
    {
        var helper = new SqlBulkCopyHelper<TestData>("#Test")
            .MapAllPublicProperties()
            .UseBracketQuoting();

        const int nrOrRows = 15;
        var testData = TestDataFactory.GetTestData(nrOrRows).ToList();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        var sql = helper.CreateTableScript();
        await connection.ExecuteAsync(sql);

        await helper.BulkInsertAsync(connection, testData);
        
        var result = connection.Query<TestData>("SELECT * FROM #Test").ToList();

        await connection.CloseAsync();
        
        result.ShouldBeEquivalentTo(testData);
    }

    [Fact]
    public async Task BulkInsert_ComputedValues()
    {
        var helper = new SqlBulkCopyHelper<TestData>("#Test")
            .UseBracketQuoting()
            .Map("This should works as column name, when using brackets",
                x => x.BoolColumn ? x.IntColumn * 2 : x.IntColumn);
        
        const int nrOrRows = 15;
        var testData = TestDataFactory.GetTestData(nrOrRows).ToList();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        var sql = helper.CreateTableScript();
        await connection.ExecuteAsync(sql);

        await helper.BulkInsertAsync(connection, testData);
        
        var result = connection.Query<int>("SELECT * FROM #Test").ToList();

        await connection.CloseAsync();
        
        var computedValues = testData.Select(x => x.BoolColumn ? x.IntColumn * 2 : x.IntColumn).ToList();
        
        result.ShouldBeEquivalentTo(computedValues);
    }
}