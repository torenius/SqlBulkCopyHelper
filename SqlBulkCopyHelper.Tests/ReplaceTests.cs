using System.Data;
using System.Diagnostics;
using Dapper;
using Microsoft.Data.SqlClient;
using Shouldly;

namespace SqlBulkCopyHelper.Tests;

// Same class as BulkInsertTests, so the tests share the SQL Server container
public partial class BulkInsertTests
{
    private class ReplaceRow
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public int Quantity { get; set; }
    }

    private static string NewReplaceTableName() => $"Replace_{Guid.NewGuid():N}";

    private static ReplaceRow[] ReplaceRows(params string[] names) => names.Select((x, i) => new ReplaceRow { Id = i + 1, Name = x, Quantity = i + 1 }).ToArray();

    private static SqlBulkCopyHelper<ReplaceRow> CreateReplaceHelper(string tableName) =>
        new SqlBulkCopyHelper<ReplaceRow>(tableName)
            .Map("Name", x => x.Name)
            .Map("Quantity", x => x.Quantity);

    private async Task<SqlConnection> OpenReplaceConnectionAsync()
    {
        var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    // The tests in a class run one at a time, so no other test has incoming or outgoing tables
    private static async Task ShouldHaveNoSwapTablesAsync(SqlConnection connection) =>
        (await connection.QueryAsync<string>("SELECT name FROM sys.tables WHERE name LIKE '%[_]Incoming[_]%' OR name LIKE '%[_]Outgoing[_]%'")).ShouldBeEmpty();

    private static Task<List<string?>> GetNamesAsync(SqlConnection connection, string tableName) =>
        connection.QueryAsync<string?>($"SELECT Name FROM {tableName} ORDER BY Name").ContinueWith(x => x.Result.ToList());

    [Fact]
    public async Task Replace_SwapsTheRows_AndKeepsTheTable()
    {
        await using var connection = await OpenReplaceConnectionAsync();
        var tableName = NewReplaceTableName();
        var typeName = $"Sku_{Guid.NewGuid():N}";

        await connection.ExecuteAsync($"CREATE TYPE dbo.{typeName} FROM varchar(20) NULL;");
        await connection.ExecuteAsync($"""
            CREATE TABLE dbo.{tableName}
            (
                Id int IDENTITY(1, 1) NOT NULL,
                Name nvarchar(50) COLLATE Finnish_Swedish_CI_AS NOT NULL,
                Quantity int NOT NULL CONSTRAINT DF_{tableName}_Quantity DEFAULT (1),
                Sku dbo.{typeName},
                Comment nvarchar(100) SPARSE NULL,
                RowGuid uniqueidentifier ROWGUIDCOL NOT NULL DEFAULT NEWSEQUENTIALID(),
                Total AS (Quantity * 2) PERSISTED NOT NULL,
                UpperName AS (UPPER(Name)),
                Version rowversion,
                CONSTRAINT PK_{tableName} PRIMARY KEY CLUSTERED (Id),
                CONSTRAINT UQ_{tableName}_Name UNIQUE NONCLUSTERED (Name),
                CONSTRAINT CK_{tableName}_Quantity CHECK (Quantity >= 0)
            );
            CREATE INDEX IX_{tableName}_Quantity ON dbo.{tableName} (Quantity DESC, Id) INCLUDE (Comment) WHERE Quantity > 0;
            CREATE TABLE dbo.{tableName}_Log (Id int);
            GRANT SELECT ON dbo.{tableName} TO public;
            INSERT INTO dbo.{tableName} (Name, Quantity) VALUES (N'Old A', 1), (N'Old B', 2), (N'Old C', 3);
            """);
        await connection.ExecuteAsync($"CREATE TRIGGER dbo.TR_{tableName} ON dbo.{tableName} AFTER INSERT AS INSERT INTO dbo.{tableName}_Log SELECT Id FROM inserted;");

        const string metadataSql = """
            SELECT CONCAT('index ', name) COLLATE DATABASE_DEFAULT FROM sys.indexes WHERE object_id = OBJECT_ID(@TableName)
            UNION ALL SELECT CONCAT('constraint ', name) COLLATE DATABASE_DEFAULT FROM sys.objects WHERE parent_object_id = OBJECT_ID(@TableName)
            UNION ALL SELECT CONCAT('permission ', permission_name) COLLATE DATABASE_DEFAULT FROM sys.database_permissions WHERE major_id = OBJECT_ID(@TableName)
            UNION ALL SELECT CONCAT('object ', OBJECT_ID(@TableName)) COLLATE DATABASE_DEFAULT
            """;
        var metadata = (await connection.QueryAsync<string>(metadataSql, new { TableName = $"dbo.{tableName}" })).Order().ToList();

        var rows = await CreateReplaceHelper($"dbo.{tableName}")
            .BulkReplaceAsync(connection, ReplaceRows("New A", "New B", "New C", "New D"), cancellationToken: TestContext.Current.CancellationToken);

        rows.ShouldBe(4);
        (await GetNamesAsync(connection, tableName)).ShouldBe(["New A", "New B", "New C", "New D"]);
        (await connection.QueryAsync<int>($"SELECT Total FROM {tableName} ORDER BY Name")).ShouldBe([2, 4, 6, 8]);
        (await connection.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {tableName} WHERE RowGuid IS NULL OR RowGuid = '00000000-0000-0000-0000-000000000000'")).ShouldBe(0);

        // The same object, so the names, permissions and triggers are kept, and the trigger didn't fire for the new rows
        (await connection.QueryAsync<string>(metadataSql, new { TableName = $"dbo.{tableName}" })).Order().ShouldBe(metadata);
        (await connection.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {tableName}_Log")).ShouldBe(0);

        // The new rows got Id 1-4 while the table's identity was at 3, so it must be reseeded
        var id = await connection.ExecuteScalarAsync<int>($"INSERT INTO {tableName} (Name) VALUES (N'Later'); SELECT CAST(SCOPE_IDENTITY() AS int);");
        id.ShouldBe(5);

        await ShouldHaveNoSwapTablesAsync(connection);
    }

    [Theory]
    [InlineData("heap with compression and a LOB column",
        "CREATE TABLE {0} (Id int NOT NULL, Name nvarchar(max) NULL, Quantity int NOT NULL) WITH (DATA_COMPRESSION = ROW); CREATE UNIQUE INDEX IX ON {0} (Id) WITH (IGNORE_DUP_KEY = ON);")]
    [InlineData("clustered columnstore",
        "CREATE TABLE {0} (Id int NOT NULL, Name nvarchar(50) NULL, Quantity int NOT NULL, INDEX CCI CLUSTERED COLUMNSTORE);")]
    [InlineData("clustered index and nonclustered columnstore",
        "CREATE TABLE {0} (Id int NOT NULL, Name nvarchar(50) NULL, Quantity int NOT NULL); CREATE CLUSTERED INDEX CX ON {0} (Id); CREATE NONCLUSTERED COLUMNSTORE INDEX NCCI ON {0} (Id, Quantity);")]
    [InlineData("nonclustered primary key and compressed unique clustered index",
        "CREATE TABLE {0} (Id int NOT NULL PRIMARY KEY NONCLUSTERED WITH (DATA_COMPRESSION = ROW), Name nvarchar(50) NULL, Quantity int NOT NULL); CREATE UNIQUE CLUSTERED INDEX CX ON {0} (Quantity) WITH (DATA_COMPRESSION = PAGE);")]
    [InlineData("disabled index",
        "CREATE TABLE {0} (Id int NOT NULL PRIMARY KEY, Name nvarchar(50) NULL, Quantity int NOT NULL); CREATE INDEX IX ON {0} (Name); ALTER INDEX IX ON {0} DISABLE;")]
    [InlineData("untrusted and disabled check constraints",
        "CREATE TABLE {0} (Id int NOT NULL PRIMARY KEY, Name nvarchar(50) NULL, Quantity int NOT NULL); ALTER TABLE {0} WITH NOCHECK ADD CHECK (Quantity > 0); ALTER TABLE {0} ADD CONSTRAINT CK_Disabled CHECK (Quantity > 100); ALTER TABLE {0} NOCHECK CONSTRAINT CK_Disabled;")]
    [InlineData("identity with negative increment",
        "CREATE TABLE {0} (Id int IDENTITY(-1, -1) NOT NULL PRIMARY KEY, Name nvarchar(50) NULL, Quantity int NOT NULL);")]
    public async Task Replace_TableStructures(string description, string createTable)
    {
        await using var connection = await OpenReplaceConnectionAsync();
        var tableName = "dbo." + NewReplaceTableName();
        await connection.ExecuteAsync(string.Format(createTable, tableName));

        var helper = new SqlBulkCopyHelper<ReplaceRow>(tableName).Map("Name", x => x.Name).Map("Quantity", x => x.Quantity);
        if (description.Contains("identity"))
        {
            await connection.ExecuteAsync($"INSERT INTO {tableName} (Name, Quantity) VALUES (N'Old', 1);");
        }
        else
        {
            await connection.ExecuteAsync($"INSERT INTO {tableName} (Id, Name, Quantity) VALUES (1, N'Old', 1);");
            helper.Map("Id", x => x.Id);
        }

        var rows = await helper.BulkReplaceAsync(connection, ReplaceRows("New A", "New B"), cancellationToken: TestContext.Current.CancellationToken);

        rows.ShouldBe(2);
        (await GetNamesAsync(connection, tableName)).ShouldBe(["New A", "New B"]);
        await ShouldHaveNoSwapTablesAsync(connection);
    }

    [Fact]
    public async Task Replace_QuotedNamesAndOtherSchema()
    {
        await using var connection = await OpenReplaceConnectionAsync();
        var schemaName = $"My Schema {Guid.NewGuid():N}";
        await connection.ExecuteAsync($"EXEC (N'CREATE SCHEMA [{schemaName}]');");
        await connection.ExecuteAsync($"CREATE TABLE [{schemaName}].[Odd.Table]]Name] (Id int NOT NULL PRIMARY KEY, Name nvarchar(50) NULL, Quantity int NOT NULL);");

        var tableName = $"[{schemaName}].[Odd.Table]]Name]";
        var rows = await CreateReplaceHelper(tableName).Map("Id", x => x.Id)
            .BulkReplaceAsync(connection, ReplaceRows("A", "B"), cancellationToken: TestContext.Current.CancellationToken);

        rows.ShouldBe(2);
        (await GetNamesAsync(connection, tableName)).ShouldBe(["A", "B"]);
        await ShouldHaveNoSwapTablesAsync(connection);
    }

    [Fact]
    public async Task Replace_LongTableName_IsShortened()
    {
        await using var connection = await OpenReplaceConnectionAsync();
        var tableName = NewReplaceTableName().PadRight(128, 'x');
        await connection.ExecuteAsync($"CREATE TABLE dbo.{tableName} (Id int NOT NULL PRIMARY KEY, Name nvarchar(50) NULL, Quantity int NOT NULL);");

        await CreateReplaceHelper($"dbo.{tableName}").Map("Id", x => x.Id)
            .BulkReplaceAsync(connection, ReplaceRows("A"), cancellationToken: TestContext.Current.CancellationToken);

        (await GetNamesAsync(connection, tableName)).ShouldBe(["A"]);
        await ShouldHaveNoSwapTablesAsync(connection);
    }

    [Fact]
    public async Task Replace_ForeignKey_IsCheckedBeforeTheSwap()
    {
        await using var connection = await OpenReplaceConnectionAsync();
        var tableName = NewReplaceTableName();
        await connection.ExecuteAsync($"""
            CREATE TABLE dbo.{tableName}_Parent (Id int PRIMARY KEY);
            INSERT INTO dbo.{tableName}_Parent VALUES (1), (2);
            CREATE TABLE dbo.{tableName} (Id int NOT NULL PRIMARY KEY, Name nvarchar(50) NULL, Quantity int NOT NULL REFERENCES dbo.{tableName}_Parent (Id) ON DELETE CASCADE);
            INSERT INTO dbo.{tableName} VALUES (1, N'Old', 1);
            """);
        var helper = CreateReplaceHelper($"dbo.{tableName}").Map("Id", x => x.Id);

        await helper.BulkReplaceAsync(connection, ReplaceRows("A", "B"), cancellationToken: TestContext.Current.CancellationToken);
        (await GetNamesAsync(connection, tableName)).ShouldBe(["A", "B"]);

        // Quantity 3 has no parent
        await Should.ThrowAsync<SqlException>(async () =>
            await helper.BulkReplaceAsync(connection, ReplaceRows("C", "D", "E"), cancellationToken: TestContext.Current.CancellationToken));

        (await GetNamesAsync(connection, tableName)).ShouldBe(["A", "B"]);
        await ShouldHaveNoSwapTablesAsync(connection);
    }

    [Fact]
    public async Task Replace_FailsBeforeTheSwap_TableIsUnchanged()
    {
        await using var connection = await OpenReplaceConnectionAsync();
        var tableName = NewReplaceTableName();
        await connection.ExecuteAsync($"CREATE TABLE dbo.{tableName} (Id int NOT NULL PRIMARY KEY, Name nvarchar(50) NULL, Quantity int NOT NULL); INSERT INTO dbo.{tableName} VALUES (1, N'Old', 1);");

        // Duplicate primary key
        var rows = new[] { new ReplaceRow { Id = 1, Name = "A" }, new ReplaceRow { Id = 1, Name = "B" } };
        await Should.ThrowAsync<SqlException>(async () =>
            await CreateReplaceHelper($"dbo.{tableName}").Map("Id", x => x.Id)
                .BulkReplaceAsync(connection, rows, cancellationToken: TestContext.Current.CancellationToken));

        (await GetNamesAsync(connection, tableName)).ShouldBe(["Old"]);
        await ShouldHaveNoSwapTablesAsync(connection);
    }

    [Fact]
    public async Task Replace_AsyncEnumerable_EmptySource_EmptiesTheTable()
    {
        await using var connection = await OpenReplaceConnectionAsync();
        var tableName = NewReplaceTableName();
        await connection.ExecuteAsync($"CREATE TABLE dbo.{tableName} (Id int NOT NULL PRIMARY KEY, Name nvarchar(50) NULL, Quantity int NOT NULL); INSERT INTO dbo.{tableName} VALUES (1, N'Old', 1);");

        var rows = await CreateReplaceHelper($"dbo.{tableName}").Map("Id", x => x.Id)
            .BulkReplaceAsync(connection, AsyncEnumerable.Empty<ReplaceRow>(), cancellationToken: TestContext.Current.CancellationToken);

        rows.ShouldBe(0);
        (await GetNamesAsync(connection, tableName)).ShouldBeEmpty();
        await ShouldHaveNoSwapTablesAsync(connection);
    }

    [Fact]
    public async Task Replace_ClosedConnection_IsClosedAgain()
    {
        await using var setup = await OpenReplaceConnectionAsync();
        var tableName = NewReplaceTableName();
        await setup.ExecuteAsync($"CREATE TABLE dbo.{tableName} (Id int NOT NULL PRIMARY KEY, Name nvarchar(50) NULL, Quantity int NOT NULL);");

        await using var connection = new SqlConnection(_connectionString);
        await CreateReplaceHelper($"dbo.{tableName}").Map("Id", x => x.Id)
            .BulkReplaceAsync(connection, ReplaceRows("A"), cancellationToken: TestContext.Current.CancellationToken);

        connection.State.ShouldBe(System.Data.ConnectionState.Closed);
        (await GetNamesAsync(setup, tableName)).ShouldBe(["A"]);
    }

    [Fact]
    public async Task Replace_BlockedSwap_TimesOut_TableIsUnchanged()
    {
        await using var connection = await OpenReplaceConnectionAsync();
        var tableName = NewReplaceTableName();
        await connection.ExecuteAsync($"CREATE TABLE dbo.{tableName} (Id int NOT NULL PRIMARY KEY, Name nvarchar(50) NULL, Quantity int NOT NULL); INSERT INTO dbo.{tableName} VALUES (1, N'Old', 1);");

        // A reader that holds a lock on the table
        await using var reader = await OpenReplaceConnectionAsync();
        await using var transaction = (SqlTransaction)await reader.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await reader.ExecuteAsync($"SELECT COUNT(*) FROM dbo.{tableName} WITH (TABLOCK, HOLDLOCK);", transaction: transaction);

        await Should.ThrowAsync<SqlException>(async () =>
            await CreateReplaceHelper($"dbo.{tableName}").Map("Id", x => x.Id)
                .BulkReplaceAsync(connection, ReplaceRows("New"), timeout: 2, cancellationToken: TestContext.Current.CancellationToken));

        await transaction.CommitAsync(TestContext.Current.CancellationToken);
        (await GetNamesAsync(connection, tableName)).ShouldBe(["Old"]);
        await ShouldHaveNoSwapTablesAsync(connection);
    }

    [Fact]
    public async Task Replace_WaitAtLowPriority_NewReadersAreNotBlocked()
    {
        await using var connection = await OpenReplaceConnectionAsync();
        var tableName = NewReplaceTableName();
        await connection.ExecuteAsync($"CREATE TABLE dbo.{tableName} (Id int NOT NULL PRIMARY KEY, Name nvarchar(50) NULL, Quantity int NOT NULL); INSERT INTO dbo.{tableName} VALUES (1, N'Old', 1);");

        // A reader that holds a lock on the table, so the swap has to wait
        await using var blocker = await OpenReplaceConnectionAsync();
        await using var transaction = (SqlTransaction)await blocker.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await blocker.ExecuteAsync($"SELECT COUNT(*) FROM dbo.{tableName} WITH (TABLOCK, HOLDLOCK);", transaction: transaction);

        var replace = CreateReplaceHelper($"dbo.{tableName}").Map("Id", x => x.Id)
            .BulkReplaceAsync(connection, ReplaceRows("New"), replace => replace.WaitAtLowPriority(1), cancellationToken: TestContext.Current.CancellationToken)
            .AsTask();

        // Wait until the swap waits for the lock at low priority
        await using var monitor = await OpenReplaceConnectionAsync();
        var waiting = false;
        for (var i = 0; i < 100 && !waiting; i++)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
            waiting = await monitor.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.dm_os_waiting_tasks WHERE wait_type = 'LCK_M_SCH_M_LOW_PRIORITY'") > 0;
        }

        waiting.ShouldBeTrue();

        // A new reader gets the old rows right away, instead of waiting behind the swap
        await using var newReader = await OpenReplaceConnectionAsync();
        var stopwatch = Stopwatch.StartNew();
        var names = await newReader.QueryAsync<string>($"SELECT Name FROM dbo.{tableName}", commandTimeout: 5);
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
        names.ShouldBe(["Old"]);
        replace.IsCompleted.ShouldBeFalse();

        await transaction.CommitAsync(TestContext.Current.CancellationToken);
        (await replace).ShouldBe(1);
        (await GetNamesAsync(connection, tableName)).ShouldBe(["New"]);
    }

    [Fact]
    public async Task Replace_ReferencedByForeignKey_NotSupported()
    {
        await using var connection = await OpenReplaceConnectionAsync();
        var tableName = NewReplaceTableName();
        await connection.ExecuteAsync($"""
            CREATE TABLE dbo.{tableName} (Id int NOT NULL PRIMARY KEY, Name nvarchar(50) NULL, Quantity int NOT NULL);
            CREATE TABLE dbo.{tableName}_Child (ParentId int CONSTRAINT FK_{tableName}_Child REFERENCES dbo.{tableName} (Id));
            """);

        var exception = await Should.ThrowAsync<NotSupportedException>(async () =>
            await CreateReplaceHelper($"dbo.{tableName}").BulkReplaceAsync(connection, ReplaceRows("A"), cancellationToken: TestContext.Current.CancellationToken));

        exception.Message.ShouldContain($"FK_{tableName}_Child");
        await ShouldHaveNoSwapTablesAsync(connection);
    }

    [Fact]
    public async Task Replace_ChangeTracking_NotSupported()
    {
        await using var connection = await OpenReplaceConnectionAsync();
        var databaseName = $"Replace_{Guid.NewGuid():N}";
        await connection.ExecuteAsync($"CREATE DATABASE {databaseName}; ALTER DATABASE {databaseName} SET CHANGE_TRACKING = ON;");
        await connection.ChangeDatabaseAsync(databaseName, TestContext.Current.CancellationToken);
        await connection.ExecuteAsync("CREATE TABLE dbo.Tracked (Id int NOT NULL PRIMARY KEY, Name nvarchar(50) NULL, Quantity int NOT NULL); ALTER TABLE dbo.Tracked ENABLE CHANGE_TRACKING;");

        var exception = await Should.ThrowAsync<NotSupportedException>(async () =>
            await CreateReplaceHelper("dbo.Tracked").BulkReplaceAsync(connection, ReplaceRows("A"), cancellationToken: TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("Change Tracking");
    }

    [Theory]
    [InlineData("#Temp", "temp tables")]
    [InlineData("master.dbo.Table", "current database")]
    public async Task Replace_TableName_NotSupported(string tableName, string message)
    {
        await using var connection = await OpenReplaceConnectionAsync();

        var exception = await Should.ThrowAsync<NotSupportedException>(async () =>
            await CreateReplaceHelper(tableName).BulkReplaceAsync(connection, ReplaceRows("A"), cancellationToken: TestContext.Current.CancellationToken));

        exception.Message.ShouldContain(message);
    }

    [Fact]
    public async Task Replace_OutputColumn_NotSupported()
    {
        await using var connection = await OpenReplaceConnectionAsync();

        await Should.ThrowAsync<NotSupportedException>(async () =>
            await CreateReplaceHelper("dbo.Table").OutputColumn(x => x.Id)
                .BulkReplaceAsync(connection, ReplaceRows("A"), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Replace_TableNotFound()
    {
        await using var connection = await OpenReplaceConnectionAsync();

        var exception = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await CreateReplaceHelper("dbo.Missing").BulkReplaceAsync(connection, ReplaceRows("A"), cancellationToken: TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("was not found");
    }

    private async Task<string> CreateReplaceTableAsync(SqlConnection connection)
    {
        var tableName = "dbo." + NewReplaceTableName();
        await connection.ExecuteAsync($"CREATE TABLE {tableName} (Id int NOT NULL PRIMARY KEY, Name nvarchar(50) NULL, Quantity int NOT NULL); INSERT INTO {tableName} VALUES (1, N'Old', 1);");
        return tableName;
    }

    private static DataTable CreateReplaceDataTable(params string[] columnNames)
    {
        var dataTable = new DataTable();
        foreach (var columnName in columnNames)
        {
            dataTable.Columns.Add(columnName, columnName == "Name" ? typeof(string) : typeof(int));
        }

        return dataTable;
    }

    [Fact]
    public async Task Replace_Extension_Entities()
    {
        await using var connection = await OpenReplaceConnectionAsync();
        var tableName = await CreateReplaceTableAsync(connection);

        var rows = await connection.BulkReplaceAsync(tableName, ReplaceRows("A", "B"), cancellationToken: TestContext.Current.CancellationToken);

        rows.ShouldBe(2);
        (await GetNamesAsync(connection, tableName)).ShouldBe(["A", "B"]);
        await ShouldHaveNoSwapTablesAsync(connection);
    }

    [Fact]
    public async Task Replace_Extension_AsyncEnumerable()
    {
        await using var connection = await OpenReplaceConnectionAsync();
        var tableName = await CreateReplaceTableAsync(connection);

        var rows = await connection.BulkReplaceAsync(tableName, ReplaceRows("A", "B").ToAsyncEnumerable(), cancellationToken: TestContext.Current.CancellationToken);

        rows.ShouldBe(2);
        (await GetNamesAsync(connection, tableName)).ShouldBe(["A", "B"]);
    }

    [Fact]
    public async Task Replace_DataTable_MapsByName_AndSkipsDeletedRows()
    {
        await using var connection = await OpenReplaceConnectionAsync();
        var tableName = await CreateReplaceTableAsync(connection);

        // Not in the same order as the table
        var dataTable = CreateReplaceDataTable("Quantity", "Name", "Id");
        dataTable.Rows.Add(10, "A", 1);
        dataTable.Rows.Add(20, "B", 2);
        dataTable.Rows.Add(30, "C", 3);
        dataTable.AcceptChanges();
        dataTable.Rows[1].Delete();

        var rows = await connection.BulkReplaceAsync(tableName, dataTable, cancellationToken: TestContext.Current.CancellationToken);

        rows.ShouldBe(2);
        (await connection.QueryAsync<(int, string, int)>($"SELECT Id, Name, Quantity FROM {tableName} ORDER BY Id")).ShouldBe([(1, "A", 10), (3, "C", 30)]);
        await ShouldHaveNoSwapTablesAsync(connection);
    }

    [Fact]
    public async Task Replace_DataTable_ConfigureBulkCopy_CanChangeTheMappings()
    {
        await using var connection = await OpenReplaceConnectionAsync();
        var tableName = await CreateReplaceTableAsync(connection);

        // Existing code that relies on the order of the columns, instead of their names
        var dataTable = new DataTable();
        dataTable.Columns.Add("Column1", typeof(int));
        dataTable.Columns.Add("Column2", typeof(string));
        dataTable.Columns.Add("Column3", typeof(int));
        dataTable.Rows.Add(1, "A", 10);

        await connection.BulkReplaceAsync(tableName, dataTable, configureBulkCopy: bulkCopy =>
        {
            bulkCopy.ColumnMappings.Clear();
            bulkCopy.ColumnMappings.Add(0, 0);
            bulkCopy.ColumnMappings.Add(1, 1);
            bulkCopy.ColumnMappings.Add(2, 2);
        }, cancellationToken: TestContext.Current.CancellationToken);

        (await connection.QueryAsync<(int, string, int)>($"SELECT Id, Name, Quantity FROM {tableName}")).ShouldBe([(1, "A", 10)]);
    }

    [Fact]
    public async Task Replace_DataTable_ColumnMissingInTable_TableIsUnchanged()
    {
        await using var connection = await OpenReplaceConnectionAsync();
        var tableName = await CreateReplaceTableAsync(connection);

        var dataTable = CreateReplaceDataTable("Id", "Name", "Quantity", "Missing");
        dataTable.Rows.Add(1, "A", 10, 0);

        var exception = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await connection.BulkReplaceAsync(tableName, dataTable, cancellationToken: TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("[Missing]");
        (await GetNamesAsync(connection, tableName)).ShouldBe(["Old"]);
        await ShouldHaveNoSwapTablesAsync(connection);
    }

    [Fact]
    public async Task Replace_DataReader_FromAnotherConnection_IsNotDisposed()
    {
        await using var connection = await OpenReplaceConnectionAsync();
        var tableName = await CreateReplaceTableAsync(connection);
        var sourceTableName = await CreateReplaceTableAsync(connection);
        await connection.ExecuteAsync($"INSERT INTO {sourceTableName} VALUES (2, N'A', 2), (3, N'B', 3);");

        await using var sourceConnection = await OpenReplaceConnectionAsync();
        await using var command = new SqlCommand($"SELECT Quantity, Name, Id FROM {sourceTableName} WHERE Id > 1", sourceConnection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);

        var rows = await connection.BulkReplaceAsync(tableName, reader, cancellationToken: TestContext.Current.CancellationToken);

        rows.ShouldBe(2);
        reader.IsClosed.ShouldBeFalse();
        (await connection.QueryAsync<(int, string, int)>($"SELECT Id, Name, Quantity FROM {tableName} ORDER BY Id")).ShouldBe([(2, "A", 2), (3, "B", 3)]);
        await ShouldHaveNoSwapTablesAsync(connection);
    }

    [Theory]
    [InlineData("#Temp", "temp tables")]
    [InlineData("master.dbo.Table", "current database")]
    public async Task Replace_DataTable_TableName_NotSupported(string tableName, string message)
    {
        await using var connection = await OpenReplaceConnectionAsync();

        var exception = await Should.ThrowAsync<NotSupportedException>(async () =>
            await connection.BulkReplaceAsync(tableName, CreateReplaceDataTable("Id"), cancellationToken: TestContext.Current.CancellationToken));

        exception.Message.ShouldContain(message);
    }
}
