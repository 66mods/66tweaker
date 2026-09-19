using System.Runtime.InteropServices;
using static Tweaker.Infrastructure.Windows.Gpu.Amd.AdlxNative;

namespace Tweaker.Infrastructure.Windows.Gpu.Amd;

/// <summary>
/// The real ADLX session: one <c>ADLXInitialize</c>, every object released and <c>ADLXTerminate</c> on
/// dispose. Feature objects are fetched fresh for each call rather than cached, so a value is always read
/// from the driver and never from a stale handle.
/// </summary>
internal sealed class AdlxDriver : IAdlxDriver
{
    // Table positions from the SDK headers. IADLXSystem has no IADLXInterface prefix.
    private const int SystemGetGpus = 1;
    private const int SystemGet3DSettingsServices = 7;
    private const int ListSize = 3;
    private const int GpuListAt = 11;
    private const int GpuName = 7;
    private const int ServicesGetAntiLag = 3;
    private const int ServicesGetChill = 4;
    private const int ServicesGetBoost = 5;
    private const int ServicesGetImageSharpening = 6;
    private const int ServicesGetEnhancedSync = 7;
    private const int ServicesGetWaitForVerticalRefresh = 8;
    private const int ServicesGetFrameRateTargetControl = 9;
    private const int ServicesGetAntiAliasing = 10;
    private const int ServicesGetMorphologicalAntiAliasing = 11;
    private const int ServicesGetAnisotropicFiltering = 12;
    private const int ServicesGetTessellation = 13;
    private const int ServicesGetRadeonSuperResolution = 14;
    private const int FeatureIsSupported = 3;

    /// <summary>Where each feature keeps its read and write methods; every feature reads IsSupported at 3.</summary>
    private static readonly IReadOnlyDictionary<AdlxSetting, (int Getter, int Reader, int Writer, bool Mode)> Layout =
        new Dictionary<AdlxSetting, (int, int, int, bool)>
        {
            [AdlxSetting.AntiLag] = (ServicesGetAntiLag, 4, 5, false),
            [AdlxSetting.Chill] = (ServicesGetChill, 4, 8, false),
            [AdlxSetting.Boost] = (ServicesGetBoost, 4, 7, false),
            [AdlxSetting.ImageSharpening] = (ServicesGetImageSharpening, 4, 7, false),
            [AdlxSetting.EnhancedSync] = (ServicesGetEnhancedSync, 4, 5, false),
            [AdlxSetting.WaitForVerticalRefresh] = (ServicesGetWaitForVerticalRefresh, 5, 6, true),
            [AdlxSetting.FrameRateTargetControl] = (ServicesGetFrameRateTargetControl, 4, 7, false),
            [AdlxSetting.AntiAliasingMode] = (ServicesGetAntiAliasing, 4, 7, true),
            [AdlxSetting.MorphologicalAntiAliasing] = (ServicesGetMorphologicalAntiAliasing, 4, 5, false),
            [AdlxSetting.AnisotropicFiltering] = (ServicesGetAnisotropicFiltering, 4, 6, false),
            [AdlxSetting.TessellationMode] = (ServicesGetTessellation, 4, 6, true),
            [AdlxSetting.RadeonSuperResolution] = (ServicesGetRadeonSuperResolution, 4, 5, false)
        };

    private IntPtr system;
    private IntPtr services;
    private readonly List<IntPtr> gpuHandles = [];
    private readonly List<AdlxGpu> gpus = [];

    /// <summary>
    /// ADLX is one runtime per process, not one per caller: a second ADLXInitialize returns the same
    /// system object and the first ADLXTerminate tears it down under everyone. Sessions are short (one
    /// preview, one write) and the view model starts them from thread-pool tasks that can overlap, so
    /// one session at a time is the rule, held from Open until Dispose.
    /// </summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private AdlxDriver() { }

    internal static AdlxDriver Open()
    {
        Gate.Wait();
        AdlxDriver driver;
        IntPtr system;
        try
        {
            Require(Initialize(RequestedVersion, out system), "ADLXInitialize");
            driver = new AdlxDriver { system = system };
        }
        catch
        {
            Gate.Release();
            throw;
        }
        try
        {
            Require(Slot<GetObjectFn>(system, SystemGet3DSettingsServices)(system, out driver.services), "Get3DSettingsServices");
            Require(Slot<GetObjectFn>(system, SystemGetGpus)(system, out var list), "GetGPUs");
            try
            {
                var count = Slot<SizeFn>(list, ListSize)(list);
                for (var index = 0u; index < count; index++)
                {
                    Require(Slot<AtFn>(list, GpuListAt)(list, index, out var gpu), "GPUList.At");
                    driver.gpuHandles.Add(gpu);
                    var name = Succeeded(Slot<GetTextFn>(gpu, GpuName)(gpu, out var text)) && text != IntPtr.Zero
                        ? Marshal.PtrToStringAnsi(text) ?? "AMD GPU"
                        : "AMD GPU";
                    driver.gpus.Add(new((int)index, name));
                }
            }
            finally { Release(list); }
            return driver;
        }
        catch
        {
            driver.Dispose();
            throw;
        }
    }

    public IReadOnlyList<AdlxGpu> Gpus => gpus;

    public bool IsSupported(AdlxGpu gpu, AdlxSetting setting)
    {
        var feature = Feature(gpu, setting);
        if (feature == IntPtr.Zero) return false;
        try
        {
            return Succeeded(Slot<GetBoolFn>(feature, FeatureIsSupported)(feature, out var supported)) && supported != 0;
        }
        finally { Release(feature); }
    }

    public int? Read(AdlxGpu gpu, AdlxSetting setting)
    {
        var (_, reader, _, mode) = Layout[setting];
        var feature = Feature(gpu, setting);
        if (feature == IntPtr.Zero) return null;
        try
        {
            if (mode)
                return Succeeded(Slot<GetIntFn>(feature, reader)(feature, out var value)) ? value : null;
            return Succeeded(Slot<GetBoolFn>(feature, reader)(feature, out var enabled)) ? (enabled != 0 ? 1 : 0) : null;
        }
        finally { Release(feature); }
    }

    public void Write(AdlxGpu gpu, AdlxSetting setting, int value)
    {
        var (_, _, writer, mode) = Layout[setting];
        var feature = Feature(gpu, setting);
        if (feature == IntPtr.Zero) throw new AdlxException(AdlxResult.NotSupported, setting.ToString());
        try
        {
            var result = mode
                ? Slot<SetIntFn>(feature, writer)(feature, value)
                : Slot<SetBoolFn>(feature, writer)(feature, value != 0 ? (byte)1 : (byte)0);
            Require(result, $"{setting}.Set");
        }
        finally { Release(feature); }
    }

    /// <summary>The feature object for one GPU, or zero when the services refuse it; the caller releases it.</summary>
    private IntPtr Feature(AdlxGpu gpu, AdlxSetting setting)
    {
        var (getter, _, _, _) = Layout[setting];
        int result;
        IntPtr feature;
        if (setting == AdlxSetting.RadeonSuperResolution)
            result = Slot<GetObjectFn>(services, getter)(services, out feature);
        else
            result = Slot<GetObjectForGpuFn>(services, getter)(services, gpuHandles[gpu.Index], out feature);
        return Succeeded(result) ? feature : IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var gpu in gpuHandles) Release(gpu);
        gpuHandles.Clear();
        Release(services);
        services = IntPtr.Zero;
        if (system == IntPtr.Zero) return;
        system = IntPtr.Zero;
        try { Terminate(); }
        finally { Gate.Release(); }
    }
}

internal sealed class AdlxDriverFactory : IAdlxDriverFactory
{
    public bool IsAvailable => AdlxNative.IsAvailable;
    public IAdlxDriver Open() => AdlxDriver.Open();
}
