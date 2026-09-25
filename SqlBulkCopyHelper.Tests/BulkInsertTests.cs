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

    [Fact]
    public async Task BulkInsert_DisposesEnumerator()
    {
        var helper = new SqlBulkCopyHelper<int>("#Test")
            .Map("IntColumn");

        var disposed = false;
        var values = Track(Enumerable.Range(1, 10), () => disposed = true);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await helper.BulkInsertAsync(connection, values, createTableIfNotExists: true, cancellationToken: TestContext.Current.CancellationToken);

        disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task BulkInsert_UseInternalTransaction()
    {
        var helper = new SqlBulkCopyHelper<int>("#Test")
            .Map("IntColumn");

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(helper.CreateTableScript());

        var rows = await helper.BulkInsertAsync(connection, Enumerable.Range(1, 10),
            sqlBulkCopyOptions: SqlBulkCopyOptions.UseInternalTransaction, cancellationToken: TestContext.Current.CancellationToken);

        rows.ShouldBe(10);
        (await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM #Test")).ShouldBe(10);
    }

    [Fact]
    public async Task BulkInsert_CreateTableIfNotExists_ClosedConnection()
    {
        var tableName = $"dbo.Test_{Guid.NewGuid():N}";
        var helper = new SqlBulkCopyHelper<int>(tableName)
            .Map("IntColumn");

        await using var connection = new SqlConnection(_connectionString);

        var rows = await helper.BulkInsertAsync(connection, Enumerable.Range(1, 10), createTableIfNotExists: true,
            cancellationToken: TestContext.Current.CancellationToken);

        rows.ShouldBe(10);
        connection.State.ShouldBe(System.Data.ConnectionState.Closed);
        (await connection.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {tableName}")).ShouldBe(10);
    }

    [Fact]
    public async Task BulkInsert_CreateTableIfNotExists_RollbackOnFailure()
    {
        var tableName = $"dbo.Test_{Guid.NewGuid():N}";
        var helper = new SqlBulkCopyHelper<int>(tableName)
            .Map("IntColumn");

        var disposed = false;
        var values = Track(ThrowAfter(Enumerable.Range(1, 10), 5), () => disposed = true);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await helper.BulkInsertAsync(connection, values, createTableIfNotExists: true, cancellationToken: TestContext.Current.CancellationToken));

        disposed.ShouldBeTrue();
        (await connection.ExecuteScalarAsync<int?>($"SELECT OBJECT_ID('{tableName}')")).ShouldBeNull();
    }

    [Fact]
    public async Task BulkInsert_ExternalTransaction_IsNotCommitted()
    {
        var helper = new SqlBulkCopyHelper<int>("#Test")
            .Map("IntColumn");

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(helper.CreateTableScript());

        await using (var transaction = connection.BeginTransaction())
        {
            await helper.BulkInsertAsync(connection, Enumerable.Range(1, 10), createTableIfNotExists: true,
                sqlTransaction: transaction, cancellationToken: TestContext.Current.CancellationToken);

            (await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM #Test", transaction: transaction)).ShouldBe(10);

            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        (await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM #Test")).ShouldBe(0);
    }

    [Fact]
    public async Task BulkInsert_ConfigureBulkCopy()
    {
        var notifications = 0;
        var timeout = -1;
        var helper = new SqlBulkCopyHelper<int>("#Test")
            .Map("IntColumn")
            .ConfigureBulkCopy(bulkCopy =>
            {
                timeout = bulkCopy.BulkCopyTimeout;
                bulkCopy.BulkCopyTimeout = 120;
                bulkCopy.NotifyAfter = 2;
                bulkCopy.SqlRowsCopied += (_, _) => notifications++;
            });

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var rows = await helper.BulkInsertAsync(connection, Enumerable.Range(1, 10), createTableIfNotExists: true,
            timeout: 45, cancellationToken: TestContext.Current.CancellationToken);

        rows.ShouldBe(10);
        timeout.ShouldBe(45); // Helper settings are applied before the configuration
        notifications.ShouldBe(5);
    }

    [Fact]
    public async Task BulkInsert_SqlConnectionExtension_ConfigureBulkCopy()
    {
        var configured = false;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var rows = await connection.BulkInsertAsync("#Test", "IntColumn", Enumerable.Range(1, 10), createTableIfNotExists: true,
            configureBulkCopy: _ => configured = true, cancellationToken: TestContext.Current.CancellationToken);

        rows.ShouldBe(10);
        configured.ShouldBeTrue();
    }

    private class NewTypes
    {
        public ulong ULongColumn { get; set; }
        public DateOnly DateOnlyColumn { get; set; }
        public TimeOnly TimeOnlyColumn { get; set; }
        public DateOnly? NullableDateOnlyColumn { get; set; }
    }

    [Fact]
    public async Task BulkInsert_ULongDateOnlyTimeOnly()
    {
        var helper = new SqlBulkCopyHelper<NewTypes>("#Test")
            .MapAllPublicProperties();

        var testData = new List<NewTypes>
        {
            new()
            {
                ULongColumn = ulong.MaxValue,
                DateOnlyColumn = new DateOnly(2026, 9, 25),
                TimeOnlyColumn = new TimeOnly(13, 37, 42, 123),
                NullableDateOnlyColumn = null
            }
        };

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await helper.BulkInsertAsync(connection, testData, createTableIfNotExists: true, cancellationToken: TestContext.Current.CancellationToken);

        await using var command = new SqlCommand("SELECT ULongColumn, DateOnlyColumn, TimeOnlyColumn, NullableDateOnlyColumn FROM #Test", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);

        (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
        reader.GetDecimal(0).ShouldBe(ulong.MaxValue);
        reader.GetFieldValue<DateOnly>(1).ShouldBe(testData[0].DateOnlyColumn);
        reader.GetFieldValue<TimeOnly>(2).ShouldBe(testData[0].TimeOnlyColumn);
        reader.IsDBNull(3).ShouldBeTrue();
    }

    private static IEnumerable<T> Track<T>(IEnumerable<T> source, Action onDispose)
    {
        try
        {
            foreach (var item in source)
            {
                yield return item;
            }
        }
        finally
        {
            onDispose();
        }
    }

    private static IEnumerable<T> ThrowAfter<T>(IEnumerable<T> source, int count)
    {
        var i = 0;
        foreach (var item in source)
        {
            if (i++ == count)
            {
                throw new InvalidOperationException("Failing on purpose");
            }

            yield return item;
        }
    }
}