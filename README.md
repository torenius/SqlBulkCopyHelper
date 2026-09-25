# SqlBulkCopyHelper
This library makes it possible to use IEnumerable<T> together with [SqlBulkCopy](https://learn.microsoft.com/en-us/dotnet/api/microsoft.data.sqlclient.sqlbulkcopy), by wrapping the list of values in a DataReader.  
This makes it possible to stream insert data and drastically reduce the memory footprint.

It's inspired by its Postgres counterpart [PostgreSQLCopyHelper](https://github.com/PostgreSQLCopyHelper/PostgreSQLCopyHelper)

## Installing

SqlBulkCopyHelper targets .NET 10 and uses [Microsoft.Data.SqlClient](https://www.nuget.org/packages/Microsoft.Data.SqlClient).

```
dotnet add package SqlBulkCopyHelper
```

## Basic Usage

The simplest way to use it is to have a predefined class that contains all columns you like to insert to an existing table or temp table.

```csharp
public class TestData
{
    public bool BoolColumn { get; set; }
    public byte ByteColumn { get; set; }
    public byte[] ByteArrayColumn{ get; set; }
    public short ShortColumn { get; set; }
    public int IntColumn { get; set; }
    public long LongColumn { get; set; }
    public decimal DecimalColumn { get; set; }
    public double DoubleColumn { get; set; }
    public DateTime DateTimeColumn { get; set; }
    public Guid GuidColumn { get; set; }
    public string StringColumn { get; set; }
    public int? NullableIntColumn { get; set; }
    public char CharColumn { get; set; }
}
```

Then you could just use one of the extension methods directly on a SqlConnection.
```csharp
var testData = new List<TestData>(); // You can have it as a list in memory or get it as an IEnumerable from another source
var connection = new SqlConnection("your connection string");
var numberOfRowsInserted = await connection.BulkInsertAsync("dbo.MyTable", testData); // This is a helper method that basically use .MapAllPublicProperties() as in the example below
```

## Save your data to a temp table for future processing
```csharp
var helper = new SqlBulkCopyHelper<TestData>("#Test") // The name of the table you like to insert into
            .MapAllPublicProperties() // Use the predefined mapping that maps all columns
            .UseBracketQuoting() // To make sure that the table and column names always get [] around them
            .RemoveMap("LongColumn"); // Lets say we are not interested in the LongColumn, but still like to use the automapping

await using var connection = new SqlConnection("your connection string");
await connection.OpenAsync();

var sql = helper.CreateTableScript(); // Creates a "CREATE TABLE #Test" script with all the columns that was mapped
await connection.ExecuteAsync(sql);

await helper.BulkInsertAsync(connection, testData);

var result = connection.Query<TestData>("SELECT * FROM #Test").ToList(); // Here we could do an Insert/Update/Merge or something else with the data in #Test

await connection.CloseAsync();
```

### Create the table and transactions
Instead of running `CreateTableScript` yourself you can let `BulkInsertAsync` create the table if it doesn't exist.
```csharp
await helper.BulkInsertAsync(connection, testData, createTableIfNotExists: true);
```
The CREATE TABLE and the insert succeed or fail together:
- If you don't provide a transaction, `BulkInsertAsync` starts one and commits it when the insert is done. If something fails, the table creation is rolled back as well.
- If you provide a transaction with `sqlTransaction`, everything runs in it and you are responsible for commit or rollback.
- If you use `SqlBulkCopyOptions.UseInternalTransaction`, SqlBulkCopy handles the transaction for the insert and the CREATE TABLE runs before it.

Without `createTableIfNotExists` no transaction is started by the helper.

### Configure SqlBulkCopy
If you need to change settings on the underlying SqlBulkCopy, for example BatchSize or progress notifications, use `ConfigureBulkCopy`.
It's called after the helper has applied its own settings, so you can also override them.
```csharp
var helper = new SqlBulkCopyHelper<TestData>("dbo.Test")
            .MapAllPublicProperties()
            .ConfigureBulkCopy(bulkCopy =>
            {
                bulkCopy.BatchSize = 10_000;
                bulkCopy.NotifyAfter = 50_000;
                bulkCopy.SqlRowsCopied += (_, e) => Console.WriteLine($"{e.RowsCopied} rows copied");
            });

await helper.BulkInsertAsync(connection, testData);
```

It's also possible to use it in the SqlConnection extension.
```csharp
await connection.BulkInsertAsync("dbo.Test", testData, configureBulkCopy: bulkCopy => bulkCopy.BatchSize = 10_000);
```

### Naming convention
`MapAllPublicProperties` will by default just use PropertyInfo.Name  
You can change the behavior by providing a function.
```csharp
helper.MapAllPublicProperties(propertyInfo => propertyInfo.Name.ToLower());
```

It's also possible to use that function in the SqlConnection extension.
```csharp
await connection.BulkInsertAsync("#Test", testData, propertyInfo => propertyInfo.Name.ToLower());
```

`MapAllPublicProperties` only maps instance properties with a public getter. Static properties, indexers and write-only properties are skipped.

Column names are case-insensitive, like in SQL Server. Mapping `"id"` after `"Id"` replaces the first mapping, and `RemoveMap("ID")` removes it.

### Table names and quoting
The table name can be a multipart name like `dbo.MyTable`, and the parts can already be quoted like `[dbo].[My.Table]`.
- With `UseBracketQuoting()` every part is quoted, `dbo.MyTable` becomes `[dbo].[MyTable]`. Parts that already are quoted are kept as they are.
- Without `UseBracketQuoting()` the table name is used exactly as you provided it.

The SqlConnection extensions always use bracket quoting.

## Do your own mapping
There are a few mapping options, the simplest is just an expression:
```csharp
var helper = new SqlBulkCopyHelper<Test>("dbo.Test")
            .Map("BoolColumn", x => x.BoolColumn)
            .Map("ByteColumn", x => x.ByteColumn);

await helper.BulkInsertAsync(connection, testData);
```

Since it's an expression you have some options, like concat a name:
```csharp
public class Person
{
    public string FirstName { get; set; }
    public string LastName { get; set; }
}
    
var helper = new SqlBulkCopyHelper<Person>("dbo.Person")
            .Map("FirstName", x => x.FirstName)
            .Map("LastName", x => x.LastName)
            .Map("Fullname", x => string.Concat(x.FirstName, " ", x.LastName));

await helper.BulkInsertAsync(connection, persons);
```

You are not forced to have a class for the mapping. You could have just a list of integers.
```csharp
var numbers = new List<int>();
var helper = new SqlBulkCopyHelper<int>("dbo.Test")
            .Map("IntColumn"); // In this scenario you just have to map the column name

await helper.BulkInsertAsync(connection, numbers);
```

### Dynamic
For example if you use Dapper without defining a class you get back DapperRow that you can cast to Dictionary<string, object> and use in the mapper.

```csharp
var data = new List<Dictionary<string, object>>();
var helper = new SqlBulkCopyHelper<Dictionary<string, object>>("dbo.Test")
            .Map("Id", x => x["Id"], typeof(int))
            .Map("Name", x => x["Name"], typeof(string));
```

Or if you like to go the more reflection based way you can choose your own PropertyInfo for the mapping.
```csharp
var properties = typeof(TestData).GetProperties().Where(x => x.PropertyType == typeof(int));
var helper = new SqlBulkCopyHelper<TestData>("#Test")
    .MapProperties(properties);
```
The MapProperties method will convert the properties to an expression and automatically choose the property name as the database column name.   
If you like to define your own column name you could provide a naming function:
```csharp
var helper = new SqlBulkCopyHelper<TestData>("#Test")
    .MapProperties(properties, propertyInfo => propertyInfo.Name.ToLower());
```
or the properties one by one
```csharp
var helper = new SqlBulkCopyHelper<TestData>("#Test");
var properties = typeof(TestData).GetProperties().Where(x => x.PropertyType == typeof(int));
foreach (var property in properties)
{
    helper.MapProperty(property, property.Name.ToLower());
}
```
I hope that I added enough helper methods to make it easy to create your own extension methods that fit your use case.

## Column types and nullability
`CreateTableScript` and `GetColumnInfo` use `SchemaDefinitionMapping` to decide the database type of each column, for example `int` becomes `int` and `string` becomes `nvarchar(max)`.
The mappings are meant for staging tables, so they might be on the "bigger" side. You can change or add mappings:
```csharp
helper.SchemaDefinitionMapping[typeof(decimal)] = "numeric(18,2)";
```
- Nullable types (`int?`) and enums use the mapping of their underlying type.
- If a mapped type has no mapping, `CreateTableScript` and `GetColumnInfo` throw an exception that tells you which column it is. `BulkInsertAsync` without `createTableIfNotExists` doesn't need a mapping, so types that SqlBulkCopy supports (like `SqlInt32`) still work.

With `CreateTableScript(columnsAreAlwaysNullable: false)` the columns get `null` or `not null`:
- Value types are `not null`, `Nullable<T>` (`int?`) is `null`.
- Reference types (`string`, `byte[]`) are `null` when mapped with `.Map(...)`.
- With `MapAllPublicProperties`, `MapProperties` and `MapProperty` the nullable reference type annotations are used, so `string` is `not null` and `string?` is `null`. If the nullable context is disabled they are `null`.

## DisguisedDataReader.cs
SqlBulkCopy only accepts DataTable and DbDataReader as its input. DataTable forces you to load all the data into memory before inserting it into your database.
DbDataReader makes it possible to stream insert data, but you have to implement the reader yourself or use a library for it.
I needed to move a large amount of data between two databases and got memory problems with DataTable, that's why I started looking into other options.
DisguisedDataReader will wrap your IEnumerable<T> into a DbDataReader making it possible to stream insert with SqlBulkCopy.

The values are fetched when they are read, and each mapping is only called once per row, even if the value is read several times.
