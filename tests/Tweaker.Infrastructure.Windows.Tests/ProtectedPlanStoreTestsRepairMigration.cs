
using FluentAssertions;
using Tweaker.Infrastructure.Windows.Operations.Process;
using Tweaker.Infrastructure.Windows.Privilege;

namespace Tweaker.Infrastructure.Windows.Tests;

public sealed class ProtectedPlanStoreTestsRepairMigration
{
    [Fact]
    public void RepairCatalog_ContainsOnlyCompiledReversibleOrReadOnlyPrivilegedRepairs()
    {
        var operations = PrivilegedRepairOperations.Create(new FixedProcessRunner());

        operations.Select(item => item.Descriptor.Id).Should().BeEquivalentTo("repair.verify-system", "repair.fix-wifi");
        operations.Should().OnlyContain(item => item.Descriptor.RequiresElevation);
        operations.Should().NotContain(item => item.Descriptor.Id == "repair.reset-winsock");
    }

}
