using System.Runtime.InteropServices;
using System.Text;

namespace Tweaker.Infrastructure.Windows.Gpu.Intel;

/// <summary>The real IGCL session: one <c>ctlInit</c>, closed on dispose.</summary>
internal sealed class IgclDriver : IIgclDriver
{
    private IntPtr api;
    private readonly IntPtr[] devices;
    private readonly List<IgclAdapter> adapters = [];

    private IgclDriver(IntPtr api, IntPtr[] devices)
    {
        this.api = api;
        this.devices = devices;
        for (var index = 0; index < devices.Length; index++)
            adapters.Add(new(index, $"Intel adapter {index + 1}"));
    }

    internal static IgclDriver Open()
    {
        var args = CtlInitArgs.Create();
        IgclNative.Require(IgclNative.ctlInit(ref args, out var api), "ctlInit");
        try
        {
            var count = 0u;
            IgclNative.Require(IgclNative.ctlEnumerateDevices(api, ref count, null), "ctlEnumerateDevices");
            var devices = new IntPtr[count];
            if (count > 0)
                IgclNative.Require(IgclNative.ctlEnumerateDevices(api, ref count, devices), "ctlEnumerateDevices");
            return new IgclDriver(api, devices.Take((int)count).ToArray());
        }
        catch
        {
            IgclNative.ctlClose(api);
            throw;
        }
    }

    public IReadOnlyList<IgclAdapter> Adapters => adapters;

    public IReadOnlyList<IgclFeatureCapability> Capabilities(IgclAdapter adapter)
    {
        var handle = devices[adapter.Index];
        var caps = new Ctl3DFeatureCaps { Size = (uint)Marshal.SizeOf<Ctl3DFeatureCaps>(), Version = 0 };
        // First call reports the count; the second fills a caller-owned array of that size.
        IgclNative.Require(IgclNative.ctlGetSupported3DCapabilities(handle, ref caps), "ctlGetSupported3DCapabilities");
        var count = (int)caps.NumSupportedFeatures;
        if (count == 0) return [];
        var stride = Marshal.SizeOf<Ctl3DFeatureDetails>();
        var buffer = Marshal.AllocHGlobal(stride * count);
        try
        {
            for (var offset = 0; offset < stride * count; offset++) Marshal.WriteByte(buffer, offset, 0);
            caps.FeatureDetails = buffer;
            IgclNative.Require(IgclNative.ctlGetSupported3DCapabilities(handle, ref caps), "ctlGetSupported3DCapabilities");
            var result = new List<IgclFeatureCapability>();
            for (var index = 0; index < Math.Min(count, (int)caps.NumSupportedFeatures); index++)
            {
                var details = Marshal.PtrToStructure<Ctl3DFeatureDetails>(buffer + index * stride);
                result.Add(details.ValueType switch
                {
                    CtlPropertyValueType.Enum => new(details.FeatureType, details.ValueType, details.PerAppSupport != 0,
                        SupportedEnumTypes: details.Value.EnumSupportedTypes),
                    CtlPropertyValueType.Int32 => new(details.FeatureType, details.ValueType, details.PerAppSupport != 0,
                        IntMin: details.Value.IntMin, IntMax: details.Value.IntMax),
                    CtlPropertyValueType.UInt32 => new(details.FeatureType, details.ValueType, details.PerAppSupport != 0,
                        UIntMin: details.Value.UIntMin, UIntMax: details.Value.UIntMax),
                    _ => new(details.FeatureType, details.ValueType, details.PerAppSupport != 0)
                });
            }
            return result;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public IgclFeatureValue? Read(IgclAdapter adapter, Ctl3DFeature feature, CtlPropertyValueType type, string application)
    {
        var (request, name) = Build(feature, type, application, set: false, default);
        try
        {
            var result = IgclNative.ctlGetSet3DFeature(devices[adapter.Index], ref request);
            if (result is not (CtlResult.Success or CtlResult.SuccessStillOpenByAnotherCaller)) return null;
            return Decode(type, request.Value);
        }
        finally { Marshal.FreeHGlobal(name); }
    }

    public void Write(IgclAdapter adapter, Ctl3DFeature feature, CtlPropertyValueType type, string application, IgclFeatureValue value)
    {
        var (request, name) = Build(feature, type, application, set: true, value);
        try { IgclNative.Require(IgclNative.ctlGetSet3DFeature(devices[adapter.Index], ref request), $"ctlGetSet3DFeature({feature})"); }
        finally { Marshal.FreeHGlobal(name); }
    }

    private static (Ctl3DFeatureGetSet Request, IntPtr Name) Build(Ctl3DFeature feature, CtlPropertyValueType type,
        string application, bool set, IgclFeatureValue? value)
    {
        var bytes = Encoding.ASCII.GetBytes(application);
        if (bytes.Length > sbyte.MaxValue) throw new ArgumentException("The application name is too long for IGCL.", nameof(application));
        var name = Marshal.AllocHGlobal(bytes.Length + 1);
        Marshal.Copy(bytes, 0, name, bytes.Length);
        Marshal.WriteByte(name, bytes.Length, 0);
        var request = new Ctl3DFeatureGetSet
        {
            Size = (uint)Marshal.SizeOf<Ctl3DFeatureGetSet>(),
            Version = 0,
            FeatureType = feature,
            ApplicationName = name,
            ApplicationNameLength = (sbyte)bytes.Length,
            Set = set ? (byte)1 : (byte)0,
            ValueType = type,
            Value = value is null ? default : Encode(type, value)
        };
        return (request, name);
    }

    internal static CtlProperty Encode(CtlPropertyValueType type, IgclFeatureValue value)
    {
        var property = new CtlProperty();
        switch (type)
        {
            case CtlPropertyValueType.Enum: property.EnumType = value.Raw; break;
            case CtlPropertyValueType.Bool: property.Enable = value.Enable ? (byte)1 : (byte)0; break;
            case CtlPropertyValueType.Int32: property.Enable = value.Enable ? (byte)1 : (byte)0; property.IntValue = unchecked((int)value.Raw); break;
            case CtlPropertyValueType.UInt32: property.Enable = value.Enable ? (byte)1 : (byte)0; property.UIntValue = value.Raw; break;
            case CtlPropertyValueType.Float: property.Enable = value.Enable ? (byte)1 : (byte)0; property.FloatValue = BitConverter.Int32BitsToSingle(unchecked((int)value.Raw)); break;
            default: throw new NotSupportedException($"IGCL value type {type} is not written by this product.");
        }
        return property;
    }

    internal static IgclFeatureValue Decode(CtlPropertyValueType type, CtlProperty property) => type switch
    {
        CtlPropertyValueType.Enum => new(true, property.EnumType),
        CtlPropertyValueType.Bool => new(property.Enable != 0, property.Enable != 0 ? 1u : 0u),
        CtlPropertyValueType.Int32 => new(property.Enable != 0, unchecked((uint)property.IntValue)),
        CtlPropertyValueType.UInt32 => new(property.Enable != 0, property.UIntValue),
        CtlPropertyValueType.Float => new(property.Enable != 0, unchecked((uint)BitConverter.SingleToInt32Bits(property.FloatValue))),
        _ => throw new NotSupportedException($"IGCL value type {type} is not read by this product.")
    };

    public void Dispose()
    {
        if (api == IntPtr.Zero) return;
        IgclNative.ctlClose(api);
        api = IntPtr.Zero;
    }
}

internal sealed class IgclDriverFactory : IIgclDriverFactory
{
    public bool IsAvailable => IgclNative.IsAvailable;
    public IIgclDriver Open() => IgclDriver.Open();
}
