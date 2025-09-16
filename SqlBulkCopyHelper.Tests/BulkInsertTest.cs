using Dapper;
using Microsoft.Data.SqlClient;
using Shouldly;

namespace SqlBulkCopyHelper.Tests;

public class BulkInsertTest(MsSqlFixture fixture) : IClassFixture<MsSqlFixture>
{
    private readonly string _connectionString = fixture.Container.GetConnectionString();
    
    [Fact]
    public async Task BulkInsert_DifferentDataTypes()
    {
        var helper = new SqlBulkCopyHelper<TestData>("#Test")
            .MapAllPublicProperties()
            .UseBracketQuoting()
            .MapDecimal("DecimalColumn", x => x.DecimalColumn, 25, 10);

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
}