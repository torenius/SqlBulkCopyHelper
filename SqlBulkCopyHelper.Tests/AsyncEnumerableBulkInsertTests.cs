using System.Runtime.CompilerServices;
using Dapper;
using Microsoft.Data.SqlClient;
using Shouldly;

namespace SqlBulkCopyHelper.Tests;

// Same class as BulkInsertTests, so the tests share the SQL Server container
public partial class BulkInsertTests
{
    [Fact]
    public async Task BulkInsert_AsyncEnumerable()
    {
        var helper = new SqlBulkCopyHelper<TestData>("#Test")
            .MapAllPublicProperties();

        var testData = TestDataFactory.GetTestData(15).ToList();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var rows = await helper.BulkInsertAsync(connection, ToAsync(testData), createTableIfNotExists: true,
            cancellationToken: TestContext.Current.CancellationToken);

        rows.ShouldBe(15);
        var result = (await connection.QueryAsync<TestData>("SELECT * FROM #Test")).ToList();
        result.ShouldBeEquivalentTo(testData);
    }

    [Fact]
    public async Task BulkInsert_AsyncEnumerable_DisposesSource()
    {
        var helper = new SqlBulkCopyHelper<int>("#Test")
            .Map("IntColumn");

        var disposed = false;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await helper.BulkInsertAsync(connection, ToAsync(Enumerable.Range(1, 10), () => disposed = true), createTableIfNotExists: true,
            cancellationToken: TestContext.Current.CancellationToken);

        disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task BulkInsert_AsyncEnumerable_RollbackOnFailure()
    {
        var tableName = $"dbo.Test_{Guid.NewGuid():N}";
        var helper = new SqlBulkCopyHelper<int>(tableName)
            .Map("IntColumn");

        var disposed = false;
        var values = ToAsync(ThrowAfter(Enumerable.Range(1, 10), 5), () => disposed = true);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await helper.BulkInsertAsync(connection, values, createTableIfNotExists: true, cancellationToken: TestContext.Current.CancellationToken));

        disposed.ShouldBeTrue();
        (await connection.ExecuteScalarAsync<int?>($"SELECT OBJECT_ID('{tableName}')")).ShouldBeNull();
    }

    [Fact]
    public async Task BulkInsert_AsyncEnumerable_CancellationTokenReachesSource()
    {
        var helper = new SqlBulkCopyHelper<int>("#Test")
            .Map("IntColumn");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        CancellationToken receivedToken = default;

        async IAsyncEnumerable<int> Source([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            receivedToken = cancellationToken;
            for (var i = 1; i <= 10_000; i++)
            {
                if (i == 50)
                {
                    cts.Cancel();
                }

                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                yield return i;
            }
        }

        await using var connection = new SqlConnection(_connectionString);

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await helper.BulkInsertAsync(connection, Source(), createTableIfNotExists: true, cancellationToken: cts.Token));

        receivedToken.ShouldBe(cts.Token);
        connection.State.ShouldBe(System.Data.ConnectionState.Closed);
    }

    [Fact]
    public async Task BulkInsert_SqlConnectionExtension_AsyncEnumerable()
    {
        var testData = TestDataFactory.GetTestData(15).ToList();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var rows = await connection.BulkInsertAsync("#Test", ToAsync(testData), createTableIfNotExists: true,
            cancellationToken: TestContext.Current.CancellationToken);

        rows.ShouldBe(15);
        var result = (await connection.QueryAsync<TestData>("SELECT * FROM #Test")).ToList();
        result.ShouldBeEquivalentTo(testData);
    }

    [Fact]
    public async Task BulkInsert_SqlConnectionExtension_AsyncEnumerableOfStrings()
    {
        List<string?> values = ["A", null, "C"];

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var rows = await connection.BulkInsertAsync("#Test", "Value", ToAsync(values), createTableIfNotExists: true,
            cancellationToken: TestContext.Current.CancellationToken);

        rows.ShouldBe(3);
        var result = (await connection.QueryAsync<string?>("SELECT Value FROM #Test")).ToList();
        result.ShouldBe(values, ignoreOrder: true);
    }

    [Fact]
    public void SqlConnectionExtension_AsyncEnumerableOfStringsWithoutColumnName_Throws()
    {
        using var connection = new SqlConnection();

        Should.Throw<InvalidOperationException>(() =>
        {
            _ = connection.BulkInsertAsync("#Test", ToAsync(new List<string> { "A" }));
        });
    }

    // Like an EF Core DbSet, that implements both IEnumerable and IAsyncEnumerable
    [Fact]
    public async Task BulkInsert_TypeWithBothEnumerableInterfaces_UsesIEnumerableByDefault()
    {
        var helper = new SqlBulkCopyHelper<int>("#Test")
            .Map("IntColumn");

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var source = new BothEnumerables([1, 2, 3]);
        var rows = await helper.BulkInsertAsync(connection, source, createTableIfNotExists: true,
            cancellationToken: TestContext.Current.CancellationToken);

        rows.ShouldBe(3);
        source.Enumerated.ShouldBeTrue();
        source.AsyncEnumerated.ShouldBeFalse();

        var asyncSource = new BothEnumerables([4, 5, 6]);
        rows = await helper.BulkInsertAsync(connection, asyncSource.AsAsyncEnumerable(), createTableIfNotExists: true,
            cancellationToken: TestContext.Current.CancellationToken);

        rows.ShouldBe(3);
        asyncSource.Enumerated.ShouldBeFalse();
        asyncSource.AsyncEnumerated.ShouldBeTrue();
    }

    [Fact]
    public async Task BulkInsert_SqlConnectionExtension_TypeWithBothEnumerableInterfaces_UsesIEnumerableByDefault()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var values = new BothEnumerables([1, 2, 3]);
        await connection.BulkInsertAsync("#Values", "Value", values, createTableIfNotExists: true,
            cancellationToken: TestContext.Current.CancellationToken);

        values.Enumerated.ShouldBeTrue();
        values.AsyncEnumerated.ShouldBeFalse();

        var entities = new BothEnumerables<TestData>(TestDataFactory.GetTestData(3).ToList());
        await connection.BulkInsertAsync("#Entities", entities, createTableIfNotExists: true,
            cancellationToken: TestContext.Current.CancellationToken);

        entities.Enumerated.ShouldBeTrue();
        entities.AsyncEnumerated.ShouldBeFalse();
    }

    private sealed class BothEnumerables(List<int> values) : BothEnumerables<int>(values);

    private class BothEnumerables<T>(List<T> values) : IEnumerable<T>, IAsyncEnumerable<T>
    {
        public bool Enumerated { get; private set; }
        public bool AsyncEnumerated { get; private set; }

        public IEnumerator<T> GetEnumerator()
        {
            Enumerated = true;
            return values.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            AsyncEnumerated = true;
            return ToAsync(values).GetAsyncEnumerator(cancellationToken);
        }

        public IAsyncEnumerable<T> AsAsyncEnumerable() => this;
    }

    // The token comes from GetAsyncEnumerator, not from the caller, see [EnumeratorCancellation]
    internal static IAsyncEnumerable<T> ToAsync<T>(IEnumerable<T> source, Action? onDispose = null) =>
        ToAsyncCore(source, onDispose, CancellationToken.None);

    private static async IAsyncEnumerable<T> ToAsyncCore<T>(IEnumerable<T> source, Action? onDispose,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            foreach (var item in source)
            {
                // Makes it truly async, so the reader can't complete synchronously
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }
        }
        finally
        {
            onDispose?.Invoke();
        }
    }
}
