using Dapper;
using Microsoft.Data.SqlClient;
using Shouldly;

namespace SqlBulkCopyHelper.Tests;

// Same class as BulkInsertTests, so the tests share the SQL Server container
public partial class BulkInsertTests
{
    private class Order
    {
        public int Id { get; set; }
        public string Name { get; set; } = null!;
        public int Quantity { get; set; }
        public decimal Price { get; set; }
        public Guid RowGuid { get; set; }
        public decimal Total { get; set; }
        public int? NullableDefault { get; set; }
    }

    private const string CreateOrdersTable = """
        CREATE TABLE #Orders
        (
            Id int IDENTITY(1000, 7) PRIMARY KEY,
            Name nvarchar(100) NOT NULL,
            Quantity int NOT NULL,
            Price decimal(18, 2) NOT NULL,
            RowGuid uniqueidentifier NOT NULL DEFAULT NEWSEQUENTIALID(),
            Total AS Quantity * Price,
            NullableDefault int NULL
        );
        """;

    private static List<Order> GetOrders(int count) =>
        Enumerable.Range(1, count).Select(x => new Order { Name = "Order " + x, Quantity = x % 7, Price = x * 1.5m }).ToList();

    private static SqlBulkCopyHelper<Order> CreateOrderHelper(string tableName = "#Orders") =>
        new SqlBulkCopyHelper<Order>(tableName)
            .UseBracketQuoting()
            .Map("Name", x => x.Name)
            .Map("Quantity", x => x.Quantity)
            .Map("Price", x => x.Price)
            .OutputColumn(x => x.Id);

    [Fact]
    public async Task OutputColumn_SetsIdentityOnEntities()
    {
        var orders = GetOrders(5_000);
        var helper = CreateOrderHelper()
            .ConfigureBulkCopy(bulkCopy => bulkCopy.BatchSize = 100);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(CreateOrdersTable);

        var rows = await helper.BulkInsertAsync(connection, orders, cancellationToken: TestContext.Current.CancellationToken);

        rows.ShouldBe(orders.Count);
        orders.Select(x => x.Id).Distinct().Count().ShouldBe(orders.Count);

        var inDatabase = (await connection.QueryAsync<Order>("SELECT Id, Name FROM #Orders")).ToDictionary(x => x.Id, x => x.Name);
        foreach (var order in orders)
        {
            inDatabase[order.Id].ShouldBe(order.Name);
        }
    }

    [Fact]
    public async Task OutputColumn_AsyncEnumerable()
    {
        var orders = GetOrders(100);
        var helper = CreateOrderHelper();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(CreateOrdersTable);

        var rows = await helper.BulkInsertAsync(connection, ToAsync(orders), cancellationToken: TestContext.Current.CancellationToken);

        rows.ShouldBe(orders.Count);
        var inDatabase = (await connection.QueryAsync<Order>("SELECT Id, Name FROM #Orders")).ToDictionary(x => x.Id, x => x.Name);
        foreach (var order in orders)
        {
            inDatabase[order.Id].ShouldBe(order.Name);
        }
    }

    [Fact]
    public async Task OutputColumn_DefaultComputedAndNullValues()
    {
        var orders = GetOrders(10);
        var helper = CreateOrderHelper()
            .OutputColumn(x => x.RowGuid)
            .OutputColumn<decimal>("Total", (order, total) => order.Total = total)
            .OutputColumn(x => x.NullableDefault);

        foreach (var order in orders)
        {
            order.NullableDefault = 42;
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(CreateOrdersTable);

        await helper.BulkInsertAsync(connection, orders, cancellationToken: TestContext.Current.CancellationToken);

        var inDatabase = (await connection.QueryAsync<Order>("SELECT * FROM #Orders")).ToDictionary(x => x.Id);
        foreach (var order in orders)
        {
            order.RowGuid.ShouldNotBe(Guid.Empty);
            order.RowGuid.ShouldBe(inDatabase[order.Id].RowGuid);
            order.Total.ShouldBe(order.Quantity * order.Price);
            order.NullableDefault.ShouldBeNull();
        }
    }

    [Fact]
    public async Task OutputColumn_TargetTableWithTrigger()
    {
        var tableName = $"dbo.Orders_{Guid.NewGuid():N}";
        var helper = CreateOrderHelper(tableName);
        var orders = GetOrders(10);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(CreateOrdersTable.Replace("#Orders", tableName));
        await connection.ExecuteAsync($"CREATE TRIGGER {tableName}_Trigger ON {tableName} AFTER INSERT AS UPDATE t SET Quantity = t.Quantity + 1000 FROM {tableName} t JOIN inserted i ON i.Id = t.Id");

        await helper.BulkInsertAsync(connection, orders, cancellationToken: TestContext.Current.CancellationToken);

        orders.ShouldAllBe(x => x.Id > 0);
        (await connection.ExecuteScalarAsync<int>($"SELECT MIN(Quantity) FROM {tableName}")).ShouldBeGreaterThanOrEqualTo(1000);
    }

    [Fact]
    public async Task OutputColumn_ValueEntities()
    {
        var ids = new Dictionary<string, int>();
        var helper = new SqlBulkCopyHelper<string>("#Names")
            .Map("Name")
            .OutputColumn<int>("Id", (name, id) => ids[name] = id);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync("CREATE TABLE #Names (Id int IDENTITY PRIMARY KEY, Name nvarchar(10) NOT NULL)");

        await helper.BulkInsertAsync(connection, ["A", "B", "C"], cancellationToken: TestContext.Current.CancellationToken);

        var inDatabase = (await connection.QueryAsync<(int Id, string Name)>("SELECT Id, Name FROM #Names")).ToDictionary(x => x.Name, x => x.Id);
        ids.ShouldBe(inDatabase, ignoreOrder: true);
    }

    [Fact]
    public async Task OutputColumn_NoMappedColumns_InsertsDefaultValues()
    {
        var helper = new SqlBulkCopyHelper<Order>("#Orders")
            .OutputColumn(x => x.Id);
        var orders = GetOrders(3);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync("CREATE TABLE #Orders (Id int IDENTITY PRIMARY KEY, Created datetime2 NOT NULL DEFAULT SYSUTCDATETIME())");

        var rows = await helper.BulkInsertAsync(connection, orders, cancellationToken: TestContext.Current.CancellationToken);

        rows.ShouldBe(3);
        orders.Select(x => x.Id).ShouldBe([1, 2, 3], ignoreOrder: true);
    }

    [Fact]
    public async Task OutputColumn_EmptySource()
    {
        var helper = CreateOrderHelper();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(CreateOrdersTable);

        var rows = await helper.BulkInsertAsync(connection, new List<Order>(), cancellationToken: TestContext.Current.CancellationToken);

        rows.ShouldBe(0);
    }

    [Fact]
    public async Task OutputColumn_CreateTableIfNotExists_ClosedConnection()
    {
        var tableName = $"dbo.Test_{Guid.NewGuid():N}";
        var values = Enumerable.Range(1, 10).Select(x => new Order { Quantity = x }).ToList();
        var helper = new SqlBulkCopyHelper<Order>(tableName)
            .Map("Quantity", x => x.Quantity)
            .OutputColumn<int>("Quantity", (order, quantity) => order.Total = quantity * 2);

        await using var connection = new SqlConnection(_connectionString);

        var rows = await helper.BulkInsertAsync(connection, values, createTableIfNotExists: true, cancellationToken: TestContext.Current.CancellationToken);

        rows.ShouldBe(10);
        connection.State.ShouldBe(System.Data.ConnectionState.Closed);
        values.ShouldAllBe(x => x.Total == x.Quantity * 2);
    }

    [Fact]
    public async Task OutputColumn_FailureDuringCopy_InsertsNothingAndDropsTempTables()
    {
        var helper = CreateOrderHelper();
        var orders = ThrowAfter(GetOrders(10), 5);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(CreateOrdersTable);

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await helper.BulkInsertAsync(connection, orders, cancellationToken: TestContext.Current.CancellationToken));

        (await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM #Orders")).ShouldBe(0);
        (await CountHelperTempTables(connection)).ShouldBe(0);
    }

    [Fact]
    public async Task OutputColumn_DropsTempTables()
    {
        var helper = CreateOrderHelper();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(CreateOrdersTable);

        await helper.BulkInsertAsync(connection, GetOrders(10), cancellationToken: TestContext.Current.CancellationToken);

        (await CountHelperTempTables(connection)).ShouldBe(0);
    }

    [Fact]
    public async Task OutputColumn_ExternalTransaction_IsNotCommitted()
    {
        var helper = CreateOrderHelper();
        var orders = GetOrders(10);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(CreateOrdersTable);

        await using (var transaction = connection.BeginTransaction())
        {
            await helper.BulkInsertAsync(connection, orders, sqlTransaction: transaction, cancellationToken: TestContext.Current.CancellationToken);
            orders.ShouldAllBe(x => x.Id > 0);

            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        (await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM #Orders")).ShouldBe(0);
    }

    [Fact]
    public async Task OutputColumn_NullInNonNullableValue_Throws()
    {
        var helper = CreateOrderHelper()
            .OutputColumn<int>("NullableDefault", (_, _) => { });

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(CreateOrdersTable);

        var exception = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await helper.BulkInsertAsync(connection, GetOrders(1), cancellationToken: TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("NullableDefault");
    }

    [Fact]
    public async Task OutputColumn_KeepIdentity_Throws()
    {
        var helper = CreateOrderHelper();

        await using var connection = new SqlConnection(_connectionString);

        await Should.ThrowAsync<NotSupportedException>(async () =>
            await helper.BulkInsertAsync(connection, GetOrders(1), sqlBulkCopyOptions: SqlBulkCopyOptions.KeepIdentity,
                cancellationToken: TestContext.Current.CancellationToken));

        connection.State.ShouldBe(System.Data.ConnectionState.Closed);
    }

    [Fact]
    public void OutputColumn_InvalidExpression_Throws()
    {
        var helper = new SqlBulkCopyHelper<Order>("#Orders");

        Should.Throw<ArgumentException>(() => helper.OutputColumn(x => x.Id + 1));
        Should.Throw<ArgumentException>(() => helper.OutputColumn(x => x.Name.Length));
        Should.Throw<InvalidOperationException>(() => new SqlBulkCopyHelper<int>("#Test").OutputColumn(x => x));
    }

    private static Task<int> CountHelperTempTables(SqlConnection connection) =>
        connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tempdb.sys.tables WHERE name LIKE '#SqlBulkCopyHelper[_]%'");
}
