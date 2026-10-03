using Dapper;
using Microsoft.Data.SqlClient;
using Shouldly;

namespace SqlBulkCopyHelper.Tests;

// Same class as BulkInsertTests, so the tests share the SQL Server container
public partial class BulkInsertTests
{
    private class Product
    {
        public int Id { get; set; }
        public int TenantId { get; set; }
        public string Sku { get; set; } = null!;
        public string? Name { get; set; }
        public decimal Price { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    private const string CreateProductsTable = """
        CREATE TABLE #Products
        (
            Id int IDENTITY(1, 1) PRIMARY KEY,
            TenantId int NOT NULL,
            Sku varchar(20) NOT NULL,
            Name nvarchar(100) NULL,
            Price decimal(18, 2) NOT NULL,
            CreatedAt datetime2 NOT NULL,
            Version rowversion,
            UNIQUE (TenantId, Sku)
        );
        """;

    private static SqlBulkCopyHelper<Product> CreateProductHelper(string tableName = "#Products") =>
        new SqlBulkCopyHelper<Product>(tableName)
            .MapAllPublicProperties()
            .RemoveMap("Id");

    private static Product NewProduct(int tenantId, string sku, string? name = "Name", decimal price = 10) =>
        new() { TenantId = tenantId, Sku = sku, Name = name, Price = price, CreatedAt = new DateTime(2026, 1, 1) };

    private static async Task<SqlConnection> OpenWithProductsAsync(string connectionString, params Product[] existing)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(CreateProductsTable);
        await connection.ExecuteAsync(
            "INSERT INTO #Products (TenantId, Sku, Name, Price, CreatedAt) VALUES (@TenantId, @Sku, @Name, @Price, @CreatedAt)", existing);
        return connection;
    }

    private static async Task<Dictionary<string, Product>> GetProductsAsync(SqlConnection connection) =>
        (await connection.QueryAsync<Product>("SELECT * FROM #Products")).ToDictionary(x => $"{x.TenantId}:{x.Sku}");

    [Fact]
    public async Task Upsert_InsertsNewAndUpdatesExisting()
    {
        await using var connection = await OpenWithProductsAsync(_connectionString, NewProduct(1, "A", "Old A"), NewProduct(1, "B", "Old B"));

        var result = await CreateProductHelper().BulkUpsertAsync(connection,
            [NewProduct(1, "A", "New A", 11), NewProduct(1, "C", "New C"), NewProduct(2, "A", "Tenant 2 A")],
            upsert => upsert.MatchOn("TenantId", "Sku"), cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldBe(new SqlBulkUpsertResult(Inserted: 2, Updated: 1, Unchanged: 0, DuplicatesRemoved: 0));

        var products = await GetProductsAsync(connection);
        products.Count.ShouldBe(4);
        products["1:A"].Name.ShouldBe("New A");
        products["1:A"].Price.ShouldBe(11);
        products["1:B"].Name.ShouldBe("Old B");
        products["1:C"].Name.ShouldBe("New C");
        products["2:A"].Name.ShouldBe("Tenant 2 A");
    }

    [Fact]
    public async Task Upsert_EmptyTable_And_EmptySource()
    {
        await using var connection = await OpenWithProductsAsync(_connectionString);
        var helper = CreateProductHelper();

        var empty = await helper.BulkUpsertAsync(connection, [], upsert => upsert.MatchOn("TenantId", "Sku"),
            cancellationToken: TestContext.Current.CancellationToken);
        empty.ShouldBe(new SqlBulkUpsertResult(0, 0, 0, 0));

        var inserted = await helper.BulkUpsertAsync(connection, [NewProduct(1, "A"), NewProduct(1, "B")], upsert => upsert.MatchOn("TenantId", "Sku"),
            cancellationToken: TestContext.Current.CancellationToken);
        inserted.ShouldBe(new SqlBulkUpsertResult(2, 0, 0, 0));
    }

    [Fact]
    public async Task Upsert_OutputColumn_SetForInsertedUpdatedAndUnchanged()
    {
        await using var connection = await OpenWithProductsAsync(_connectionString, NewProduct(1, "A"), NewProduct(1, "B"));
        var existing = await GetProductsAsync(connection);

        List<Product> source = [NewProduct(1, "A", "Changed"), NewProduct(1, "B"), NewProduct(1, "C")];

        var result = await CreateProductHelper()
            .OutputColumn(x => x.Id)
            .BulkUpsertAsync(connection, source, upsert => upsert.MatchOn("TenantId", "Sku").OnlyUpdateWhenChanged(),
                cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldBe(new SqlBulkUpsertResult(Inserted: 1, Updated: 1, Unchanged: 1, DuplicatesRemoved: 0));

        var products = await GetProductsAsync(connection);
        source[0].Id.ShouldBe(existing["1:A"].Id); // Updated
        source[1].Id.ShouldBe(existing["1:B"].Id); // Unchanged
        source[2].Id.ShouldBe(products["1:C"].Id); // Inserted
        source[2].Id.ShouldNotBe(0);
    }

    [Fact]
    public async Task Upsert_OutputColumn_ManyRows()
    {
        var existing = Enumerable.Range(0, 1_000).Select(x => NewProduct(1, "S" + x)).ToArray();
        await using var connection = await OpenWithProductsAsync(_connectionString, existing);

        // Every other row exists, in a different order than in the table
        var source = Enumerable.Range(0, 2_000).Reverse().Select(x => NewProduct(1, "S" + x, "New")).ToList();

        var result = await CreateProductHelper()
            .OutputColumn(x => x.Id)
            .ConfigureBulkCopy(bulkCopy => bulkCopy.BatchSize = 100)
            .BulkUpsertAsync(connection, ToAsync(source), upsert => upsert.MatchOn("TenantId", "Sku"),
                cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldBe(new SqlBulkUpsertResult(Inserted: 1_000, Updated: 1_000, Unchanged: 0, DuplicatesRemoved: 0));

        var products = await GetProductsAsync(connection);
        foreach (var product in source)
        {
            product.Id.ShouldBe(products["1:" + product.Sku].Id);
        }
    }

    [Fact]
    public async Task Upsert_OnlyUpdateWhenChanged_HandlesNull()
    {
        await using var connection = await OpenWithProductsAsync(_connectionString,
            NewProduct(1, "Same"), NewProduct(1, "NullToValue", null), NewProduct(1, "ValueToNull"), NewProduct(1, "NullToNull", null));

        var versionsBefore = (await connection.QueryAsync<(string Sku, byte[] Version)>("SELECT Sku, Version FROM #Products"))
            .ToDictionary(x => x.Sku, x => x.Version);

        var result = await CreateProductHelper().BulkUpsertAsync(connection,
            [NewProduct(1, "Same"), NewProduct(1, "NullToValue", "Value"), NewProduct(1, "ValueToNull", null), NewProduct(1, "NullToNull", null)],
            upsert => upsert.MatchOn("TenantId", "Sku").OnlyUpdateWhenChanged(), cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldBe(new SqlBulkUpsertResult(Inserted: 0, Updated: 2, Unchanged: 2, DuplicatesRemoved: 0));

        var versionsAfter = (await connection.QueryAsync<(string Sku, byte[] Version)>("SELECT Sku, Version FROM #Products"))
            .ToDictionary(x => x.Sku, x => x.Version);

        versionsAfter["Same"].ShouldBe(versionsBefore["Same"]);
        versionsAfter["NullToNull"].ShouldBe(versionsBefore["NullToNull"]);
        versionsAfter["NullToValue"].ShouldNotBe(versionsBefore["NullToValue"]);
        versionsAfter["ValueToNull"].ShouldNotBe(versionsBefore["ValueToNull"]);

        var products = await GetProductsAsync(connection);
        products["1:NullToValue"].Name.ShouldBe("Value");
        products["1:ValueToNull"].Name.ShouldBeNull();
    }

    [Fact]
    public async Task Upsert_WithoutOnlyUpdateWhenChanged_UpdatesIdenticalRows()
    {
        await using var connection = await OpenWithProductsAsync(_connectionString, NewProduct(1, "A"));

        var result = await CreateProductHelper().BulkUpsertAsync(connection, [NewProduct(1, "A")],
            upsert => upsert.MatchOn("TenantId", "Sku"), cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldBe(new SqlBulkUpsertResult(Inserted: 0, Updated: 1, Unchanged: 0, DuplicatesRemoved: 0));
    }

    [Fact]
    public async Task Upsert_IgnoreOnUpdate()
    {
        await using var connection = await OpenWithProductsAsync(_connectionString, NewProduct(1, "A"));

        var existing = NewProduct(1, "A", "Updated");
        existing.CreatedAt = new DateTime(2030, 1, 1);
        var created = NewProduct(1, "B");
        created.CreatedAt = new DateTime(2030, 1, 1);

        await CreateProductHelper().BulkUpsertAsync(connection, [existing, created],
            upsert => upsert.MatchOn("TenantId", "Sku").IgnoreOnUpdate("createdat"), cancellationToken: TestContext.Current.CancellationToken);

        var products = await GetProductsAsync(connection);
        products["1:A"].Name.ShouldBe("Updated");
        products["1:A"].CreatedAt.ShouldBe(new DateTime(2026, 1, 1));
        products["1:B"].CreatedAt.ShouldBe(new DateTime(2030, 1, 1));
    }

    [Fact]
    public async Task Upsert_OnlyKeysMapped_InsertsMissing()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync("CREATE TABLE #Tags (Id int IDENTITY PRIMARY KEY, Name nvarchar(20) NOT NULL UNIQUE); INSERT INTO #Tags (Name) VALUES ('a'), ('b');");

        var ids = new Dictionary<string, int>();
        var result = await new SqlBulkCopyHelper<string>("#Tags")
            .Map("Name")
            .OutputColumn<int>("Id", (name, id) => ids[name] = id)
            .BulkUpsertAsync(connection, ["a", "c"], upsert => upsert.MatchOn("Name"), cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldBe(new SqlBulkUpsertResult(Inserted: 1, Updated: 0, Unchanged: 1, DuplicatesRemoved: 0));

        var inDatabase = (await connection.QueryAsync<(int Id, string Name)>("SELECT Id, Name FROM #Tags")).ToDictionary(x => x.Name, x => x.Id);
        ids["a"].ShouldBe(inDatabase["a"]);
        ids["c"].ShouldBe(inDatabase["c"]);
    }

    [Fact]
    public async Task Upsert_DuplicateKeys_Throws()
    {
        await using var connection = await OpenWithProductsAsync(_connectionString, NewProduct(1, "A", "Old"));

        var exception = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await CreateProductHelper().BulkUpsertAsync(connection,
                [NewProduct(1, "A", "New"), NewProduct(1, "B"), NewProduct(1, "C"), NewProduct(1, "A", "Newer"), NewProduct(1, "B")],
                upsert => upsert.MatchOn("TenantId", "Sku"), cancellationToken: TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("(1, 'A') 2 times, first at index 0 and last at index 3");
        exception.Message.ShouldContain("(1, 'B') 2 times, first at index 1 and last at index 4");

        var products = await GetProductsAsync(connection);
        products.Count.ShouldBe(1);
        products["1:A"].Name.ShouldBe("Old");
        (await CountHelperTempTables(connection)).ShouldBe(0);
    }

    [Theory]
    [InlineData(DuplicateKeyHandling.KeepFirst, "First")]
    [InlineData(DuplicateKeyHandling.KeepLast, "Last")]
    public async Task Upsert_DuplicateKeys_Keep(DuplicateKeyHandling handling, string expectedName)
    {
        await using var connection = await OpenWithProductsAsync(_connectionString, NewProduct(1, "A", "Old"));

        List<Product> source = [NewProduct(1, "A", "First"), NewProduct(1, "B", "First"), NewProduct(1, "A", "Middle"), NewProduct(1, "A", "Last"), NewProduct(1, "B", "Last")];

        var result = await CreateProductHelper()
            .OutputColumn(x => x.Id)
            .BulkUpsertAsync(connection, source, upsert => upsert.MatchOn("TenantId", "Sku").OnDuplicateKey(handling),
                cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldBe(new SqlBulkUpsertResult(Inserted: 1, Updated: 1, Unchanged: 0, DuplicatesRemoved: 3));

        var products = await GetProductsAsync(connection);
        products["1:A"].Name.ShouldBe(expectedName);
        products["1:B"].Name.ShouldBe(expectedName);

        // Only the kept rows get the output values
        source.Where(x => x.Name == expectedName).ShouldAllBe(x => x.Id > 0);
        source.Where(x => x.Name != expectedName).ShouldAllBe(x => x.Id == 0);
    }

    [Fact]
    public async Task Upsert_TargetTableWithTrigger()
    {
        var tableName = $"dbo.Products_{Guid.NewGuid():N}";
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(CreateProductsTable.Replace("#Products", tableName));
        await connection.ExecuteAsync($"INSERT INTO {tableName} (TenantId, Sku, Name, Price, CreatedAt) VALUES (1, 'A', 'Old', 1, '2026-01-01')");
        await connection.ExecuteAsync($"CREATE TRIGGER {tableName}_Trigger ON {tableName} AFTER INSERT, UPDATE AS UPDATE t SET Price = t.Price + 1000 FROM {tableName} t JOIN inserted i ON i.Id = t.Id");

        List<Product> source = [NewProduct(1, "A", "New", 1), NewProduct(1, "B", "New", 1)];
        var result = await CreateProductHelper(tableName)
            .OutputColumn(x => x.Id)
            .BulkUpsertAsync(connection, source, upsert => upsert.MatchOn("TenantId", "Sku"), cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldBe(new SqlBulkUpsertResult(Inserted: 1, Updated: 1, Unchanged: 0, DuplicatesRemoved: 0));
        source.ShouldAllBe(x => x.Id > 0);
        (await connection.ExecuteScalarAsync<decimal>($"SELECT MIN(Price) FROM {tableName}")).ShouldBe(1001);
    }

    [Fact]
    public async Task Upsert_FailureDuringCopy_ChangesNothing()
    {
        await using var connection = await OpenWithProductsAsync(_connectionString, NewProduct(1, "A", "Old"));

        var source = ThrowAfter([NewProduct(1, "A", "New"), NewProduct(1, "B"), NewProduct(1, "C")], 2);

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await CreateProductHelper().BulkUpsertAsync(connection, source, upsert => upsert.MatchOn("TenantId", "Sku"),
                cancellationToken: TestContext.Current.CancellationToken));

        var products = await GetProductsAsync(connection);
        products.Count.ShouldBe(1);
        products["1:A"].Name.ShouldBe("Old");
        (await CountHelperTempTables(connection)).ShouldBe(0);
    }

    [Fact]
    public async Task Upsert_ExternalTransaction_IsNotCommitted()
    {
        await using var connection = await OpenWithProductsAsync(_connectionString, NewProduct(1, "A", "Old"));

        await using (var transaction = connection.BeginTransaction())
        {
            var result = await CreateProductHelper().BulkUpsertAsync(connection, [NewProduct(1, "A", "New"), NewProduct(1, "B")],
                upsert => upsert.MatchOn("TenantId", "Sku"), sqlTransaction: transaction, cancellationToken: TestContext.Current.CancellationToken);

            result.ShouldBe(new SqlBulkUpsertResult(1, 1, 0, 0));
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        var products = await GetProductsAsync(connection);
        products.Count.ShouldBe(1);
        products["1:A"].Name.ShouldBe("Old");
    }

    [Fact]
    public async Task Upsert_ClosedConnection_IsClosedAgain()
    {
        var tableName = $"dbo.Products_{Guid.NewGuid():N}";
        await using (var setup = new SqlConnection(_connectionString))
        {
            await setup.ExecuteAsync(CreateProductsTable.Replace("#Products", tableName));
        }

        await using var connection = new SqlConnection(_connectionString);

        var result = await CreateProductHelper(tableName).BulkUpsertAsync(connection, [NewProduct(1, "A")],
            upsert => upsert.MatchOn("TenantId", "Sku"), cancellationToken: TestContext.Current.CancellationToken);

        result.Inserted.ShouldBe(1);
        connection.State.ShouldBe(System.Data.ConnectionState.Closed);
    }

    [Fact]
    public async Task Upsert_DropsTempTables()
    {
        await using var connection = await OpenWithProductsAsync(_connectionString, NewProduct(1, "A"));

        await CreateProductHelper()
            .OutputColumn(x => x.Id)
            .BulkUpsertAsync(connection, [NewProduct(1, "A"), NewProduct(1, "B")], upsert => upsert.MatchOn("TenantId", "Sku").OnlyUpdateWhenChanged(),
                cancellationToken: TestContext.Current.CancellationToken);

        (await CountHelperTempTables(connection)).ShouldBe(0);
    }

    public static TheoryData<string, Action<SqlBulkUpsertOptions>, SqlBulkCopyOptions, Type> InvalidUpsertOptions => new()
    {
        { "No MatchOn", _ => { }, SqlBulkCopyOptions.Default, typeof(InvalidOperationException) },
        { "MatchOn not mapped", upsert => upsert.MatchOn("Id"), SqlBulkCopyOptions.Default, typeof(InvalidOperationException) },
        { "IgnoreOnUpdate not mapped", upsert => upsert.MatchOn("Sku").IgnoreOnUpdate("Missing"), SqlBulkCopyOptions.Default, typeof(InvalidOperationException) },
        { "KeepIdentity", upsert => upsert.MatchOn("Sku"), SqlBulkCopyOptions.KeepIdentity, typeof(NotSupportedException) },
        { "UseInternalTransaction", upsert => upsert.MatchOn("Sku"), SqlBulkCopyOptions.UseInternalTransaction, typeof(NotSupportedException) },
    };

    [Theory]
    [MemberData(nameof(InvalidUpsertOptions))]
    public async Task Upsert_InvalidOptions_ThrowsBeforeOpeningConnection(string _, Action<SqlBulkUpsertOptions> configure, SqlBulkCopyOptions sqlBulkCopyOptions,
        Type expectedException)
    {
        await using var connection = new SqlConnection(_connectionString);

        var exception = await Should.ThrowAsync<Exception>(async () =>
            await CreateProductHelper().BulkUpsertAsync(connection, [NewProduct(1, "A")], configure, sqlBulkCopyOptions: sqlBulkCopyOptions,
                cancellationToken: TestContext.Current.CancellationToken));

        exception.ShouldBeOfType(expectedException);
        connection.State.ShouldBe(System.Data.ConnectionState.Closed);
    }

    [Fact]
    public void UpsertOptions_InvalidArguments_Throws()
    {
        var options = new SqlBulkUpsertOptions();

        Should.Throw<ArgumentException>(() => options.MatchOn());
        Should.Throw<ArgumentException>(() => options.MatchOn("Id", " "));
        Should.Throw<ArgumentException>(() => options.IgnoreOnUpdate());
        Should.Throw<ArgumentOutOfRangeException>(() => options.OnDuplicateKey((DuplicateKeyHandling)42));
    }
}
