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
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var sql = helper.CreateTableScript();
        await connection.ExecuteAsync(sql);

        await helper.BulkInsertAsync(connection, testData, cancellationToken: TestContext.Current.CancellationToken);

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
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var sql = helper.CreateTableScript();
        await connection.ExecuteAsync(sql);

        await helper.BulkInsertAsync(connection, testData, cancellationToken: TestContext.Current.CancellationToken);

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

    private enum IntEnum
    {
        A = 1,
        B = 2
    }

    private enum ByteEnum : byte
    {
        X = 10,
        Y = 20
    }

    private class EnumTest
    {
        public IntEnum IntEnumColumn { get; set; }
        public ByteEnum ByteEnumColumn { get; set; }
        public IntEnum? NullableIntEnumColumn { get; set; }
    }

    [Fact]
    public async Task BulkInsert_Enums()
    {
        var helper = new SqlBulkCopyHelper<EnumTest>("#Test")
            .MapAllPublicProperties();

        var testData = new List<EnumTest>
        {
            new() { IntEnumColumn = IntEnum.A, ByteEnumColumn = ByteEnum.X, NullableIntEnumColumn = IntEnum.B },
            new() { IntEnumColumn = IntEnum.B, ByteEnumColumn = ByteEnum.Y, NullableIntEnumColumn = null }
        };

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await helper.BulkInsertAsync(connection, testData, createTableIfNotExists: true, cancellationToken: TestContext.Current.CancellationToken);

        var result = (await connection.QueryAsync<EnumTest>("SELECT * FROM #Test ORDER BY IntEnumColumn")).ToList();

        result.ShouldBeEquivalentTo(testData);
    }

    [Fact]
    public async Task BulkInsert_QuotedTableNameWithDotAndApostrophe()
    {
        var tableName = $"dbo.[Test.O'Brien_{Guid.NewGuid():N}]";
        var helper = new SqlBulkCopyHelper<int>(tableName)
            .UseBracketQuoting()
            .Map("IntColumn");

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        // Twice, so the second call has to find the existing table with OBJECT_ID
        await helper.BulkInsertAsync(connection, Enumerable.Range(1, 5), createTableIfNotExists: true, cancellationToken: TestContext.Current.CancellationToken);
        await helper.BulkInsertAsync(connection, Enumerable.Range(1, 5), createTableIfNotExists: true, cancellationToken: TestContext.Current.CancellationToken);

        (await connection.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {tableName}")).ShouldBe(10);
    }

    [Fact]
    public async Task BulkInsert_QuotedTempTable_CreateTableIfNotExistsTwice()
    {
        var helper = new SqlBulkCopyHelper<int>("#Test")
            .UseBracketQuoting()
            .Map("IntColumn");

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await helper.BulkInsertAsync(connection, Enumerable.Range(1, 5), createTableIfNotExists: true, cancellationToken: TestContext.Current.CancellationToken);
        await helper.BulkInsertAsync(connection, Enumerable.Range(1, 5), createTableIfNotExists: true, cancellationToken: TestContext.Current.CancellationToken);

        (await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM #Test")).ShouldBe(10);
    }

    [Fact]
    public async Task BulkInsert_TypeWithoutSchemaDefinitionMapping_WorksWithoutCreateTable()
    {
        // SqlBulkCopy supports SqlTypes, even if there is no SchemaDefinitionMapping for them
        var helper = new SqlBulkCopyHelper<int>("#Test")
            .Map("IntColumn", x => new System.Data.SqlTypes.SqlInt32(x));

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync("CREATE TABLE #Test (IntColumn int)");

        var rows = await helper.BulkInsertAsync(connection, Enumerable.Range(1, 5), cancellationToken: TestContext.Current.CancellationToken);

        rows.ShouldBe(5);
        (await connection.ExecuteScalarAsync<int>("SELECT SUM(IntColumn) FROM #Test")).ShouldBe(15);
    }

    [Fact]
    public async Task BulkInsert_AlreadyCancelled_DoesNotOpenConnection()
    {
        var helper = new SqlBulkCopyHelper<int>("#Test")
            .Map("IntColumn");

        await using var connection = new SqlConnection(_connectionString);

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await helper.BulkInsertAsync(connection, Enumerable.Range(1, 5), createTableIfNotExists: true, cancellationToken: new CancellationToken(canceled: true)));

        connection.State.ShouldBe(System.Data.ConnectionState.Closed);
    }

    [Fact]
    public async Task BulkInsert_CancelledDuringInsert_RollsBackAndClosesConnection()
    {
        var tableName = $"dbo.Test_{Guid.NewGuid():N}";
        var helper = new SqlBulkCopyHelper<int>(tableName)
            .Map("IntColumn")
            .ConfigureBulkCopy(bulkCopy => bulkCopy.BatchSize = 10);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var values = Enumerable.Range(1, 10_000).Select(x =>
        {
            if (x == 100)
            {
                cts.Cancel();
            }

            return x;
        });

        await using var connection = new SqlConnection(_connectionString);

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await helper.BulkInsertAsync(connection, values, createTableIfNotExists: true, cancellationToken: cts.Token));

        connection.State.ShouldBe(System.Data.ConnectionState.Closed);
        (await connection.ExecuteScalarAsync<int?>($"SELECT OBJECT_ID('{tableName}')")).ShouldBeNull();
    }

    [Fact]
    public async Task BulkInsert_SqlConnectionExtension_MapAllPublicProperties()
    {
        var testData = TestDataFactory.GetTestData(15).ToList();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var rows = await connection.BulkInsertAsync("#Test", testData, createTableIfNotExists: true,
            cancellationToken: TestContext.Current.CancellationToken);

        rows.ShouldBe(15);
        var result = (await connection.QueryAsync<TestData>("SELECT * FROM #Test")).ToList();
        result.ShouldBeEquivalentTo(testData);
    }

    [Fact]
    public async Task BulkInsert_SqlConnectionExtension_ColumnNameFunc()
    {
        var testData = TestDataFactory.GetTestData(5).ToList();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await connection.BulkInsertAsync("#Test", testData, propertyInfo => "col_" + propertyInfo.Name, createTableIfNotExists: true,
            cancellationToken: TestContext.Current.CancellationToken);

        var ids = (await connection.QueryAsync<int>("SELECT col_IntColumn FROM #Test")).ToList();
        ids.ShouldBe(testData.Select(x => x.IntColumn).ToList(), ignoreOrder: true);
    }

    [Fact]
    public async Task BulkInsert_SqlConnectionExtension_ListOfStrings()
    {
        List<string?> values = ["A", null, "C"];

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var rows = await connection.BulkInsertAsync("#Test", "Value", values, createTableIfNotExists: true,
            cancellationToken: TestContext.Current.CancellationToken);

        rows.ShouldBe(3);
        var result = (await connection.QueryAsync<string?>("SELECT Value FROM #Test")).ToList();
        result.ShouldBe(values, ignoreOrder: true);
    }

    [Fact]
    public async Task BulkInsert_SqlConnectionExtension_ListOfNullableInts()
    {
        List<int?> values = [1, null, 3];

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await connection.BulkInsertAsync("#Test", "Value", values, createTableIfNotExists: true,
            cancellationToken: TestContext.Current.CancellationToken);

        var result = (await connection.QueryAsync<int?>("SELECT Value FROM #Test")).ToList();
        result.ShouldBe(values, ignoreOrder: true);
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