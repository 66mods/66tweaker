using FluentAssertions;
using Tweaker.Infrastructure.Windows.Legacy;
using Tweaker.Infrastructure.Windows.Operations.Process;

namespace Tweaker.Infrastructure.Windows.Tests;

/// <summary>
/// Locks the bundle shape that broke Full Legacy in the field. These are properties of the frozen BAT, not
/// of any machine, so they hold everywhere: if a future bundle stops containing them the verification
/// special-cases can be revisited, and if it grows new ones they are already covered.
/// </summary>
public sealed class FullLegacyBundleShapeTests
{
    private static IReadOnlyList<LegacyRegistryTarget> Targets() =>
        LegacyBundleOperation.CreateAll(new FixedProcessRunner())
            .Cast<LegacyBundleOperation>().Single(x => x.Category is null && x.Profile == LegacyBundleProfile.FullLegacy)
            .DiagnoseTargets();

    [Fact]
    public void TheBundleWritesValuesThatLaterEffectsDelete()
    {
        var targets = Targets();
        var removed = 0;
        for (var index = 0; index < targets.Count; index++)
        {
            if (targets[index].Action != LegacyRegistryAction.Write) continue;
            if (targets.Skip(index + 1).Any(later => Removes(later, targets[index]))) removed++;
        }

        // Measured at 110: the IFEO PerfOptions values written for each game executable, whose keys the
        // bundle then deletes. Reading them back at the end of the run is what failed every Full Legacy apply.
        removed.Should().BeGreaterThan(0,
            "verification must drop writes that a later effect removes, not treat them as missing");
    }

    [Fact]
    public void TheBundleWritesSomeValuesMoreThanOnce()
    {
        Targets().Where(x => x.Action == LegacyRegistryAction.Write)
            .GroupBy(x => $"{x.Hive}|{x.SubKey}|{x.ValueName}", StringComparer.OrdinalIgnoreCase)
            .Count(x => x.Count() > 1)
            .Should().BeGreaterThan(0, "only the last write to a value can be verified");
    }

    [Fact]
    public void APowerShellEffectWipesTheKeysOtherEffectsWroteInto()
    {
        // This is why verification cannot judge a write by the state of the registry at the end of the run:
        // the bundle writes 110 PerfOptions values under Image File Execution Options and then removes the
        // whole tree with Remove-Item. No registry-action analysis can see that; only reading each write
        // back at the moment it is made can.
        var scripts = LegacyBundleOperation.CreateAll(new FixedProcessRunner())
            .Cast<LegacyBundleOperation>().Single(x => x.Category is null && x.Profile == LegacyBundleProfile.FullLegacy)
            .DiagnoseEffects()
            .Where(x => x.Kind == "PowerShellMutation" &&
                x.Command.Contains("Remove-Item", StringComparison.OrdinalIgnoreCase) &&
                x.Command.Contains("Image File Execution Options", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        scripts.Should().NotBeEmpty("verification must survive a script deleting keys the run wrote into");
    }

    private static bool Removes(LegacyRegistryTarget candidate, LegacyRegistryTarget write) =>
        candidate.Hive == write.Hive && candidate.Action switch
        {
            LegacyRegistryAction.DeleteValue =>
                candidate.SubKey.Equals(write.SubKey, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.ValueName, write.ValueName, StringComparison.OrdinalIgnoreCase),
            LegacyRegistryAction.DeleteKey =>
                write.SubKey.Equals(candidate.SubKey, StringComparison.OrdinalIgnoreCase) ||
                write.SubKey.StartsWith(candidate.SubKey + "\\", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
}
