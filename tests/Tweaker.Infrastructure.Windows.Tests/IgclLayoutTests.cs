using System.Runtime.InteropServices;
using FluentAssertions;
using Tweaker.Infrastructure.Windows.Gpu.Intel;

namespace Tweaker.Infrastructure.Windows.Tests;

/// <summary>
/// The IGCL structs are handed to a C library by pointer; a field one byte out of place is a silent
/// misread of the driver's answer, or a write of the wrong value. The sizes and offsets here are the
/// ones igcl_api.h produces under MSVC on x64.
/// </summary>
public sealed class IgclLayoutTests
{
    [Fact]
    public void InitArgsMatchTheHeader()
    {
        Marshal.SizeOf<CtlInitArgs>().Should().Be(36);
        Marshal.OffsetOf<CtlInitArgs>(nameof(CtlInitArgs.AppVersion)).ToInt32().Should().Be(8);
        Marshal.OffsetOf<CtlInitArgs>(nameof(CtlInitArgs.ApplicationUid)).ToInt32().Should().Be(20);
    }

    [Fact]
    public void ThePropertyUnionIsEightBytesWithTheValueAtFour()
    {
        Marshal.SizeOf<CtlProperty>().Should().Be(8);
        Marshal.OffsetOf<CtlProperty>(nameof(CtlProperty.IntValue)).ToInt32().Should().Be(4);
        Marshal.OffsetOf<CtlProperty>(nameof(CtlProperty.EnumType)).ToInt32().Should().Be(0);
    }

    [Fact]
    public void FeatureDetailsMatchTheHeader()
    {
        Marshal.SizeOf<Ctl3DFeatureDetails>().Should().Be(72);
        Marshal.OffsetOf<Ctl3DFeatureDetails>(nameof(Ctl3DFeatureDetails.Value)).ToInt32().Should().Be(8);
        Marshal.OffsetOf<Ctl3DFeatureDetails>(nameof(Ctl3DFeatureDetails.CustomValueSize)).ToInt32().Should().Be(32);
        Marshal.OffsetOf<Ctl3DFeatureDetails>(nameof(Ctl3DFeatureDetails.CustomValue)).ToInt32().Should().Be(40);
        Marshal.OffsetOf<Ctl3DFeatureDetails>(nameof(Ctl3DFeatureDetails.PerAppSupport)).ToInt32().Should().Be(48);
        Marshal.OffsetOf<Ctl3DFeatureDetails>(nameof(Ctl3DFeatureDetails.ConflictingFeatures)).ToInt32().Should().Be(56);
        Marshal.OffsetOf<Ctl3DFeatureDetails>(nameof(Ctl3DFeatureDetails.FeatureMiscSupport)).ToInt32().Should().Be(64);
    }

    [Fact]
    public void FeatureCapsMatchTheHeader()
    {
        Marshal.SizeOf<Ctl3DFeatureCaps>().Should().Be(24);
        Marshal.OffsetOf<Ctl3DFeatureCaps>(nameof(Ctl3DFeatureCaps.NumSupportedFeatures)).ToInt32().Should().Be(8);
        Marshal.OffsetOf<Ctl3DFeatureCaps>(nameof(Ctl3DFeatureCaps.FeatureDetails)).ToInt32().Should().Be(16);
    }

    [Fact]
    public void FeatureGetSetMatchesTheHeader()
    {
        Marshal.SizeOf<Ctl3DFeatureGetSet>().Should().Be(56);
        Marshal.OffsetOf<Ctl3DFeatureGetSet>(nameof(Ctl3DFeatureGetSet.FeatureType)).ToInt32().Should().Be(8);
        Marshal.OffsetOf<Ctl3DFeatureGetSet>(nameof(Ctl3DFeatureGetSet.ApplicationName)).ToInt32().Should().Be(16);
        Marshal.OffsetOf<Ctl3DFeatureGetSet>(nameof(Ctl3DFeatureGetSet.ApplicationNameLength)).ToInt32().Should().Be(24);
        Marshal.OffsetOf<Ctl3DFeatureGetSet>(nameof(Ctl3DFeatureGetSet.Set)).ToInt32().Should().Be(25);
        Marshal.OffsetOf<Ctl3DFeatureGetSet>(nameof(Ctl3DFeatureGetSet.ValueType)).ToInt32().Should().Be(28);
        Marshal.OffsetOf<Ctl3DFeatureGetSet>(nameof(Ctl3DFeatureGetSet.Value)).ToInt32().Should().Be(32);
        Marshal.OffsetOf<Ctl3DFeatureGetSet>(nameof(Ctl3DFeatureGetSet.CustomValueSize)).ToInt32().Should().Be(40);
        Marshal.OffsetOf<Ctl3DFeatureGetSet>(nameof(Ctl3DFeatureGetSet.CustomValue)).ToInt32().Should().Be(48);
    }

    [Fact]
    public void EncodeAndDecodeRoundTripEveryValueType()
    {
        foreach (var type in new[] { CtlPropertyValueType.Bool, CtlPropertyValueType.Int32, CtlPropertyValueType.UInt32, CtlPropertyValueType.Enum })
        {
            var value = new IgclFeatureValue(true, 17);
            IgclDriver.Decode(type, IgclDriver.Encode(type, value)).Should().Be(type == CtlPropertyValueType.Bool
                ? new IgclFeatureValue(true, 1) : value, "{0}", type);
        }
        IgclDriver.Decode(CtlPropertyValueType.Int32, IgclDriver.Encode(CtlPropertyValueType.Int32, new(false, 0)))
            .Enable.Should().BeFalse();
    }

    [Fact]
    public void TheRequestedVersionIsOnePointZero() => IgclNative.RequestedVersion.Should().Be(0x00010000u);
}
