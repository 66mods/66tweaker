namespace Tweaker.Infrastructure.Windows.Gpu.Intel;

/// <summary>One Intel graphics adapter IGCL enumerated, by position; the driver does not hand out stable ids.</summary>
internal sealed record IgclAdapter(int Index, string Name);

/// <summary>
/// What an adapter says about one 3D feature: how its value is typed, whether it can be set per game,
/// and which values it accepts. The bitmask and ranges come straight from the driver so nothing is
/// written that this driver build would refuse.
/// </summary>
internal sealed record IgclFeatureCapability(
    Ctl3DFeature Feature, CtlPropertyValueType ValueType, bool PerAppSupport,
    ulong SupportedEnumTypes = 0, int IntMin = 0, int IntMax = 0, uint UIntMin = 0, uint UIntMax = 0);

/// <summary>A property as the driver holds it. <paramref name="Raw"/> is the enumerator, the integer bits, or the uint.</summary>
internal sealed record IgclFeatureValue(bool Enable, uint Raw);

/// <summary>
/// The four calls the Intel layer makes, behind an interface so the operation's snapshot, apply, verify
/// and rollback logic can be exercised against a fake driver on a PC that has no Intel GPU.
/// </summary>
internal interface IIgclDriver : IDisposable
{
    IReadOnlyList<IgclAdapter> Adapters { get; }
    IReadOnlyList<IgclFeatureCapability> Capabilities(IgclAdapter adapter);
    /// <summary>Reads one per-application property, or null when the driver refuses the read.</summary>
    IgclFeatureValue? Read(IgclAdapter adapter, Ctl3DFeature feature, CtlPropertyValueType type, string application);
    void Write(IgclAdapter adapter, Ctl3DFeature feature, CtlPropertyValueType type, string application, IgclFeatureValue value);
}

internal interface IIgclDriverFactory
{
    bool IsAvailable { get; }
    IIgclDriver Open();
}
