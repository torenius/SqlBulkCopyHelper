using Shouldly;

namespace SqlBulkCopyHelper.Tests;

public class DisguisedDataReaderTests
{
    [Fact]
    public void ReaderTest()
    {
        var helper = new SqlBulkCopyHelper<Tuple<int, int?>>("Test")
            .Map("IntColumn", x => x.Item1)
            .Map("NullableIntColumn", x => x.Item2);
        
        const int nrOrRows = 15;
        var testData = TestDataFactory.GetTestData(nrOrRows)
            .Select(x => new Tuple<int, int?>(x.IntColumn, x.NullableIntColumn))
            .ToList();

        var result = new List<Tuple<int, int?>>();
        var reader = helper.GetDataReader(testData);
        while (reader.Read())
        {
            var item1 = reader.GetInt32(0);
            int? item2 = null;
            
            if (!reader.IsDBNull(1))
            {
                item2 = reader.GetInt32(1);
            }
            
            var tuple = new Tuple<int, int?>(item1, item2);
            result.Add(tuple);
        }
        
        result.ShouldBeEquivalentTo(testData);
    }
}