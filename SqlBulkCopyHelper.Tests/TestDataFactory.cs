namespace SqlBulkCopyHelper.Tests;

public static class TestDataFactory
{
    private const string Letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    
    public static IEnumerable<TestData> GetTestData(int count)
    {
        var random = new Random();
        for (var i = 0; i < count; i++)
        {
            var isNotNull = random.Next() % 2 == 0;
            var td = new TestData
            {
                BoolColumn = isNotNull,
                ByteArrayColumn = new byte[10],
                ShortColumn = (short)random.Next(32768),
                IntColumn = random.Next(),
                LongColumn = random.NextInt64(),
                DecimalColumn = Math.Round((decimal)random.NextDouble() * 100_000_000_000, 10),
                DoubleColumn = Math.Round(random.NextDouble() * 100_000_000_000, 10),
                DateTimeColumn = DateTime.Now,
                GuidColumn = Guid.NewGuid(),
                NullableIntColumn = isNotNull ? random.Next() : null,
            };

            random.NextBytes(td.ByteArrayColumn);
            td.ByteColumn = td.ByteArrayColumn[0];

            var stringValue = new char[10];
            for (var j = 0; j < 10; j++)
            {
                stringValue[j] = Letters[random.Next(Letters.Length)];
            }
            td.StringColumn = new string(stringValue);
            td.CharColumn = td.StringColumn[0];

            yield return td;
        }
    }
}

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