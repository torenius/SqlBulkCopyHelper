# SqlBulkCopyHelper
This library makes it possible to use IEnumerable<T> together with [SqlBulkCopy](https://learn.microsoft.com/en-us/dotnet/api/system.data.sqlclient.sqlbulkcopy), by wrapping the list of values in a DataReader.  
This makes it possible to stream insert data and drastically reduce the memory footprint.

It's inspired by its Postgres counterpart [PostgreSQLCopyHelper](https://github.com/PostgreSQLCopyHelper/PostgreSQLCopyHelper)

## Installing

To install SqlBulkCopyHelper, run the following command in the Package Manager Console:

```
PM> Install-Package SqlBulkCopyHelper
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

Then you could just use one of the extensions methods directly on an SqlConnection.
```csharp
var testData = new List<TestData>(); // You can have it as a list in memory or get it as an IEnumerable from another source
var connection = new SqlConnection("your connection string");
var numberOfRowsInserted = await connection.BulkInsertAsync("dbo.MyTable", testData); // This is a helper method that basically use .MapAllPublicProperties() as in the example below
```

## Save your data to a temptable for future processing
```csharp
var helper = new SqlBulkCopyHelper<TestData>("#Test") // The name of the table you like to insert into
            .MapAllPublicProperties() // Use the predefined mapping that maps all columns
            .UseBracketQuoting() // To make sure that the create table script always add [] around the column names
            .RemoveMap("LongColumn"); // Lets say we are not interested in the LongColumn, but still like to use the automapping

await using var connection = new SqlConnection("your connection string");
await connection.OpenAsync();

var sql = helper.CreateTableScript(); // Creates a "CREATE TABLE #Test" script with all the columns that was mapped
await connection.ExecuteAsync(sql);

await helper.BulkInsertAsync(connection, testData);

var result = connection.Query<TestData>("SELECT * FROM #Test").ToList(); // Here we could do an Insert/Update/Merge or something else with the data in #Test

await connection.CloseAsync();
```

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
`MapAllPublicProperties` will default just use PropertyInfo.Name  
You can change the behavior by providing a function.
```csharp
helper.MapAllPublicProperties(propertyInfo => propertyInfo.Name.ToLower());
```

It's also possible to use that function in the SqlConnection extension.
```csharp
await connection.BulkInsertAsync("#Test", testData, propertyInfo => propertyInfo.Name.ToLower());
```

## Do you own mapping
There is a few mapping options, the simplest is just an expression:
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

Or if you like to go the more reflection base way you can choose your own PropertyInfo for the mapping.
```csharp
var properties = typeof(TestData).GetProperties().Where(x => x.PropertyType == typeof(int));
var helper = new SqlBulkCopyHelper<TestData>("#Test")
    .MapProperties(properties);
```
The PropertyType method will convert the properties to an expression and automatically choose the property name as the database column name.   
If you like to define you own column name you could provide a naming funtion:
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
I hope that I added enough helper methods to make it easy to create you own extensions methods that fit your use case.

## DisguisedDataReader.cs
SqlBulkCopy only accept DataTable and DbDataReader for its input. DataTable forces you to load all the data into memory before inserting it into your database.
DbDataReader makes it possible to stream insert data, but you have to implement the reader yourself or use a library for it.
I needed to move a large amount of data between two databases and got memory problems with DataTable, that's why I started looking into other options.
DisguisedDataReader will wrap your IEnumerable<T> into a DbDataReader making it possible to stream insert with SqlBulkCopy.

