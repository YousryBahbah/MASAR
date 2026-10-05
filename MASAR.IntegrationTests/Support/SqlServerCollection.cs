using Xunit;

namespace Masar.IntegrationTests.Support;

// One collection for every test that touches SQL Server: xUnit runs classes
// in the SAME collection one after another. That matters here because some
// operations are global by nature (the no-show/completion sweeps touch every
// matching row; the admin tests reason about "all active admins"), so two
// classes running in parallel against one database would interfere.
[CollectionDefinition(Name)]
public sealed class SqlServerCollection
{
    public const string Name = "SqlServer";
}
