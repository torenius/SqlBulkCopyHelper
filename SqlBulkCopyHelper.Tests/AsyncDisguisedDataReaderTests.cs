using System.Runtime.CompilerServices;
using Shouldly;

namespace SqlBulkCopyHelper.Tests;

public class AsyncDisguisedDataReaderTests
{
    private static readonly SqlBulkCopyHelper<int> Helper = new SqlBulkCopyHelper<int>("Test").Map("Id");

    [Fact]
    public async Task ReadAsync_ReadsAllRows()
    {
        await using var reader = Helper.GetDataReader(BulkInsertTests.ToAsync([1, 2, 3]), TestContext.Current.CancellationToken);

        var ids = new List<int>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            ids.Add(reader.GetInt32(0));
        }

        ids.ShouldBe([1, 2, 3]);
    }

    [Fact]
    public async Task HasRows_IsKnownAfterFirstReadAsync()
    {
        await using (var reader = Helper.GetDataReader(BulkInsertTests.ToAsync([1]), TestContext.Current.CancellationToken))
        {
            Should.Throw<NotSupportedException>(() => reader.HasRows);
            (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
            reader.HasRows.ShouldBeTrue();
        }

        await using (var empty = Helper.GetDataReader(BulkInsertTests.ToAsync<int>([]), TestContext.Current.CancellationToken))
        {
            (await empty.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeFalse();
            empty.HasRows.ShouldBeFalse();
        }
    }

    [Fact]
    public async Task SynchronousApi_IsNotSupported()
    {
        await using var reader = Helper.GetDataReader(BulkInsertTests.ToAsync([1]), TestContext.Current.CancellationToken);

        Should.Throw<NotSupportedException>(() => reader.Read()).Message.ShouldContain("ReadAsync");
        Should.Throw<NotSupportedException>(() => reader.GetEnumerator());
    }

    [Fact]
    public async Task DisposeAsync_DisposesSource()
    {
        var disposed = false;
        var reader = Helper.GetDataReader(BulkInsertTests.ToAsync([1, 2], () => disposed = true), TestContext.Current.CancellationToken);
        (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();

        await reader.DisposeAsync();

        disposed.ShouldBeTrue();
        reader.IsClosed.ShouldBeTrue();
    }

    [Fact]
    public async Task Dispose_DisposesSource()
    {
        var disposed = false;
        var reader = Helper.GetDataReader(BulkInsertTests.ToAsync([1, 2], () => disposed = true), TestContext.Current.CancellationToken);
        (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();

        reader.Dispose();

        disposed.ShouldBeTrue();
        reader.IsClosed.ShouldBeTrue();
    }

    [Fact]
    public async Task CancellationToken_IsPassedToSource()
    {
        using var cts = new CancellationTokenSource();
        CancellationToken receivedToken = default;

        async IAsyncEnumerable<int> Source([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            receivedToken = cancellationToken;
            await Task.Yield();
            yield return 1;
        }

        // The token should come from the reader through GetAsyncEnumerator, not from here
        await using var reader = Helper.GetDataReader(Source(CancellationToken.None), cts.Token);
        (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();

        receivedToken.ShouldBe(cts.Token);
    }
}
