using Testcontainers.MsSql;
using Testcontainers.Xunit;
using Xunit.Sdk;

namespace SqlBulkCopyHelper.Tests;

public sealed class MsSqlFixture(IMessageSink messageSink)
    : ContainerFixture<MsSqlBuilder, MsSqlContainer>(messageSink)
{
    protected override MsSqlBuilder Configure(MsSqlBuilder builder)
    {
        return builder.WithImage("mcr.microsoft.com/mssql/server:2022-latest");
    }
}