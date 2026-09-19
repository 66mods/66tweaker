using System.Runtime.InteropServices;

namespace Tweaker.Infrastructure.Windows.Gpu.Amd;

/// <summary>
/// The AMD Device Library eXtra (ADLX) entry points this product uses, bound to the <c>amdadlx64.dll</c>
/// the Adrenalin driver installs. Nothing is redistributed: when the library is absent the AMD layer
/// reports itself unavailable and writes nothing.
/// </summary>
/// <remarks>
/// ADLX is a C++ interface: every object is a pointer to a pointer to a table of function pointers, and
/// the table order is fixed by the SDK headers. <see cref="Slot{T}"/> reads one entry and turns it into a
/// delegate; the slot numbers in <see cref="AdlxDriver"/> are the positions in those headers, counted
/// from zero with the three <c>IADLXInterface</c> methods (Acquire, Release, QueryInterface) first.
/// </remarks>
internal static class AdlxNative
{
    private const string Library = "amdadlx64.dll";

    /// <summary>ADLX_MAKE_FULL_VER(1, 0, 0, 0): the first release, which every Adrenalin build with ADLX accepts.</summary>
    internal const ulong RequestedVersion = 1ul << 48;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int InitializeFn(ulong version, out IntPtr system);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int TerminateFn();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int ReleaseFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int GetObjectFn(IntPtr self, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int GetObjectForGpuFn(IntPtr self, IntPtr gpu, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate uint SizeFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int AtFn(IntPtr self, uint location, out IntPtr item);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int GetBoolFn(IntPtr self, out byte value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SetBoolFn(IntPtr self, byte value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int GetIntFn(IntPtr self, out int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SetIntFn(IntPtr self, int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int GetTextFn(IntPtr self, out IntPtr text);

    private static IntPtr library;
    private static bool? available;

    internal static bool IsAvailable
    {
        get
        {
            if (available is { } known) return known;
            try
            {
                available = NativeLibrary.TryLoad(Library, out library) &&
                    NativeLibrary.TryGetExport(library, "ADLXInitialize", out _) &&
                    NativeLibrary.TryGetExport(library, "ADLXTerminate", out _);
            }
            catch (Exception error) when (error is DllNotFoundException or BadImageFormatException)
            {
                available = false;
            }
            return available.Value;
        }
    }

    internal static InitializeFn Initialize => Export<InitializeFn>("ADLXInitialize");
    internal static TerminateFn Terminate => Export<TerminateFn>("ADLXTerminate");

    private static T Export<T>(string name) where T : Delegate
    {
        if (!IsAvailable) throw new DllNotFoundException($"{Library} is not installed on this PC.");
        return Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
    }

    /// <summary>The function at one position of an object's table.</summary>
    internal static T Slot<T>(IntPtr self, int index) where T : Delegate
    {
        if (self == IntPtr.Zero) throw new ArgumentException("ADLX object is null.", nameof(self));
        var table = Marshal.ReadIntPtr(self);
        var function = Marshal.ReadIntPtr(table, index * IntPtr.Size);
        if (function == IntPtr.Zero) throw new AdlxException(AdlxResult.UnknownInterface, $"slot {index}");
        return Marshal.GetDelegateForFunctionPointer<T>(function);
    }

    internal static void Release(IntPtr self)
    {
        if (self != IntPtr.Zero) Slot<ReleaseFn>(self, 1)(self);
    }

    internal static bool Succeeded(int result) =>
        result is (int)AdlxResult.Ok or (int)AdlxResult.AlreadyEnabled or (int)AdlxResult.AlreadyInitialized;

    internal static void Require(int result, string operation)
    {
        if (!Succeeded(result)) throw new AdlxException((AdlxResult)result, operation);
    }
}

internal sealed class AdlxException(AdlxResult result, string operation)
    : InvalidOperationException($"ADLX {operation} failed with {result}.")
{
    public AdlxResult Result { get; } = result;
}

/// <summary><c>ADLX_RESULT</c>.</summary>
internal enum AdlxResult
{
    Ok = 0,
    AlreadyEnabled,
    AlreadyInitialized,
    Fail,
    InvalidArgs,
    BadVersion,
    UnknownInterface,
    Terminated,
    AdlInitError,
    NotFound,
    InvalidObject,
    OrphanObjects,
    NotSupported,
    PendingOperation,
    GpuInactive,
    GpuInUse,
    TimeoutOperation,
    NotActive,
    ResetNeeded
}
