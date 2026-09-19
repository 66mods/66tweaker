using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Tweaker.App.Services;

/// <summary>
/// Decodes an H.264 clip frame by frame through Windows Media Foundation, straight to 32-bit pixels.
///
/// WPF's own MediaElement rides on Windows Media Player, and the people who run a tweaker have usually
/// removed Windows Media Player. Media Foundation is a different thing: the decoder every browser and
/// the Photos app use, present on every non-N edition of Windows 10 and 11. The source reader is asked
/// for the decoder's own NV12 planes, the cheapest thing it can hand out; the caller turns them into
/// pixels while it applies its colour key, one pass over the frame. Where a decoder will not give NV12,
/// the reader's video processor converts to RGB32 instead and the caller gets ready pixels.
///
/// Not thread-affine beyond "one thread at a time": the emblem drives it from its own worker thread.
/// </summary>
public sealed class MediaFoundationVideo : IDisposable
{
    private const uint FirstVideoStream = 0xFFFFFFFC;
    private const uint EndOfStream = 0x2;
    private const uint MfVersion = 0x00020070;

    private static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid FormatRgb32 = new("00000016-0000-0010-8000-00aa00389b71");
    private static readonly Guid FormatNv12 = new("3231564e-0000-0010-8000-00aa00389b71");
    private static readonly Guid MinimumDisplayAperture = new("d7388766-18fe-48c6-a177-ee894867c8c4");
    private static readonly Guid GeometricAperture = new("66758743-7e5f-400d-980a-aa8596c85696");
    private static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid EnableVideoProcessing = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
    private static readonly Guid EnableAdvancedVideoProcessing = new("0f81da2c-b537-4672-a8b2-a681b17307a3");

    private static int startups;
    private IMFSourceReader? reader;
    private int stride;
    private int codedHeight;
    private int offsetX, offsetY;

    private MediaFoundationVideo() { }

    public int Width { get; private set; }
    public int Height { get; private set; }
    /// <summary>Frames per second as the file declares them; 30 for the emblem clip.</summary>
    public double FramesPerSecond { get; private set; }
    /// <summary>What <see cref="ReadFrame"/> writes: NV12 planes from the decoder, or BGRX pixels.</summary>
    public VideoPixelFormat Format { get; private set; }
    /// <summary>Bytes one frame takes in <see cref="Format"/>: 1.5 per pixel for NV12, 4 for BGRX.</summary>
    public int BytesPerFrame => Format == VideoPixelFormat.Nv12 ? Width * Height * 3 / 2 : Width * Height * 4;

    /// <summary>True when the libraries are present; N editions without the Media Feature Pack lack them.</summary>
    public static bool IsAvailable => NativeLibrary.TryLoad("mfplat.dll", out var platform) & NativeLibrary.TryLoad("mfreadwrite.dll", out var readWrite)
        & Free(platform) & Free(readWrite);   // non-short-circuit on purpose: whichever loaded is freed

    private static bool Free(IntPtr handle)
    {
        if (handle != IntPtr.Zero) NativeLibrary.Free(handle);
        return true;
    }

    /// <summary>Opens the clip and negotiates a frame format. Throws with the HRESULT when Windows refuses.</summary>
    /// <param name="nativePlanes">False forces the RGB32 route through the reader's video processor; the tests use it as a reference.</param>
    public static MediaFoundationVideo Open(string path, bool nativePlanes = true)
    {
        if (Interlocked.Increment(ref startups) == 1)
        {
            // Undone on failure, so the next caller starts the platform again instead of running without it.
            try { Check(MFStartup(MfVersion, 0), "MFStartup"); }
            catch { Interlocked.Decrement(ref startups); throw; }
        }
        var video = new MediaFoundationVideo();
        try
        {
            // First choice: no processing at all, the decoder's NV12 planes as they are.
            if (nativePlanes)
            {
                Check(MFCreateSourceReaderFromURL(path, null, out var plain), "MFCreateSourceReaderFromURL");
                video.reader = plain;
                if (video.TryNegotiate(FormatNv12, VideoPixelFormat.Nv12)) return video;
                Marshal.ReleaseComObject(plain);
                video.reader = null;
            }

            // Otherwise the reader's video processor converts to RGB32. The two processing flags are mutually
            // exclusive (asking for both is E_INVALIDARG); the advanced one is preferred, plain is the fallback.
            Check(MFCreateAttributes(out var attributes, 1), "MFCreateAttributes");
            Check(Set(attributes, EnableAdvancedVideoProcessing, 1), "enable advanced video processing");
            var created = MFCreateSourceReaderFromURL(path, attributes, out var reader);
            if (created < 0)
            {
                Check(attributes.DeleteAllItems(), "reset attributes");
                Check(Set(attributes, EnableVideoProcessing, 1), "enable video processing");
                created = MFCreateSourceReaderFromURL(path, attributes, out reader);
            }
            Check(created, "MFCreateSourceReaderFromURL");
            video.reader = reader;
            if (!video.TryNegotiate(FormatRgb32, VideoPixelFormat.Bgrx))
                throw new InvalidOperationException("Media Foundation offers neither NV12 nor RGB32 for the clip.");
            return video;
        }
        catch
        {
            video.Dispose();
            throw;
        }
    }

    /// <summary>Asks the reader for one pixel format and, when it agrees, reads the geometry it settled on.</summary>
    private bool TryNegotiate(Guid subtype, VideoPixelFormat format)
    {
        var source = reader!;
        Check(MFCreateMediaType(out var wanted), "MFCreateMediaType");
        Check(Set(wanted, MajorType, MediaTypeVideo), "set major type");
        Check(Set(wanted, Subtype, subtype), "set subtype");
        if (source.SetCurrentMediaType(FirstVideoStream, IntPtr.Zero, wanted) < 0) return false;
        Check(source.SetStreamSelection(FirstVideoStream, true), "SetStreamSelection");

        Check(source.GetCurrentMediaType(FirstVideoStream, out var actual), "GetCurrentMediaType");
        Check(Get64(actual, FrameSize, out var size), "read frame size");
        var codedWidth = (int)(size >> 32);
        codedHeight = (int)(size & 0xFFFFFFFF);
        Format = format;
        var bytesPerPixel = format == VideoPixelFormat.Nv12 ? 1 : 4;
        stride = Get32(actual, DefaultStride, out var declared) == 0 ? unchecked((int)declared) : codedWidth * bytesPerPixel;
        FramesPerSecond = Get64(actual, FrameRate, out var rate) == 0 && (rate & 0xFFFFFFFF) != 0
            ? (double)(rate >> 32) / (rate & 0xFFFFFFFF) : 30;

        // Decoders pad the frame to macroblocks and describe the visible part as an aperture.
        Width = codedWidth;
        Height = codedHeight;
        offsetX = offsetY = 0;
        if (TryGetAperture(actual, MinimumDisplayAperture, out var area) || TryGetAperture(actual, GeometricAperture, out area))
        {
            offsetX = area.X & ~1;
            offsetY = area.Y & ~1;   // chroma samples cover two rows and two columns
            Width = Math.Min(area.Width, codedWidth - offsetX);
            Height = Math.Min(area.Height, codedHeight - offsetY);
        }
        if (format == VideoPixelFormat.Nv12) { Width &= ~1; Height &= ~1; }
        return Width > 0 && Height > 0;
    }

    private static bool TryGetAperture(IMFMediaType type, Guid key, out (int X, int Y, int Width, int Height) area)
    {
        // MFVideoArea: two MFOffset (16.16 fixed point each: short fraction, then short value) and a SIZE.
        var blob = Marshal.AllocHGlobal(16);
        try
        {
            if (type.GetBlob(ref key, blob, 16, out var written) < 0 || written != 16) { area = default; return false; }
            area = (Marshal.ReadInt16(blob, 2), Marshal.ReadInt16(blob, 6), Marshal.ReadInt32(blob, 8), Marshal.ReadInt32(blob, 12));
            return area.Width > 0 && area.Height > 0;
        }
        finally { Marshal.FreeHGlobal(blob); }
    }

    /// <summary>
    /// Copies the next frame into the buffer in <see cref="Format"/> (NV12: the luma plane, then the
    /// interleaved chroma plane at half height; BGRX: pixels top row first) and returns its presentation
    /// time. At the end of the clip it seeks back to the start and reads the first frame, so playback is
    /// one endless loop; <paramref name="wrapped"/> says when that happened.
    /// </summary>
    public long ReadFrame(byte[] frame, out bool wrapped)
    {
        var source = reader ?? throw new ObjectDisposedException(nameof(MediaFoundationVideo));
        if (frame.Length < BytesPerFrame) throw new ArgumentException("The frame buffer is too small.", nameof(frame));
        wrapped = false;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            Check(source.ReadSample(FirstVideoStream, 0, out _, out var flags, out var timestamp, out var sample), "ReadSample");
            if ((flags & EndOfStream) != 0)
            {
                var start = PropVariant.Int64(0);
                var format = Guid.Empty;
                Check(source.SetCurrentPosition(ref format, ref start), "SetCurrentPosition(0)");
                wrapped = true;
                continue;
            }
            if (sample is null) continue;   // a gap or a stream tick: nothing to show yet
            try
            {
                Check(sample.ConvertToContiguousBuffer(out var buffer), "ConvertToContiguousBuffer");
                try
                {
                    Check(buffer.Lock(out var pixels, out _, out var length), "Lock");
                    try { Copy(pixels, (int)length, frame); }
                    finally { buffer.Unlock(); }
                }
                finally { Marshal.ReleaseComObject(buffer); }
                return timestamp;
            }
            finally { Marshal.ReleaseComObject(sample); }
        }
        throw new InvalidOperationException("The clip produced no frame after several reads.");
    }

    /// <summary>Crops the decoder's padding away and straightens a bottom-up frame (negative stride).</summary>
    private void Copy(IntPtr pixels, int length, byte[] target)
    {
        var absoluteStride = Math.Abs(stride);
        if (Format == VideoPixelFormat.Nv12)
        {
            // The luma plane, then chroma rows (one per two luma rows) at the same stride. The decoder pads
            // the luma plane to whole macroblocks, and the declared frame height need not include that
            // padding, so the plane height comes from the buffer itself: 1.5 bytes per pixel in all.
            var lumaRows = length * 2 / (3 * absoluteStride);
            if (absoluteStride < offsetX + Width || lumaRows < offsetY + Height)
                throw new InvalidOperationException("The decoded frame is smaller than its declared size.");
            var luma = pixels + offsetY * absoluteStride + offsetX;
            for (var row = 0; row < Height; row++)
                Marshal.Copy(luma + row * absoluteStride, target, row * Width, Width);
            var chroma = pixels + (lumaRows + offsetY / 2) * absoluteStride + offsetX;
            var chromaStart = Width * Height;
            for (var row = 0; row < Height / 2; row++)
                Marshal.Copy(chroma + row * absoluteStride, target, chromaStart + row * Width, Width);
            return;
        }

        var rowBytes = Width * 4;
        if (absoluteStride < (offsetX + Width) * 4 || length < absoluteStride * codedHeight)
            throw new InvalidOperationException("The decoded frame is smaller than its declared size.");
        for (var row = 0; row < Height; row++)
        {
            var sourceRow = stride < 0 ? codedHeight - 1 - (offsetY + row) : offsetY + row;
            Marshal.Copy(pixels + sourceRow * absoluteStride + offsetX * 4, target, row * rowBytes, rowBytes);
        }
    }

    public void Dispose()
    {
        if (reader is not null)
        {
            Marshal.ReleaseComObject(reader);
            reader = null;
        }
        if (Interlocked.Decrement(ref startups) == 0) _ = MFShutdown();
    }

    private static void Check(int hresult, string step)
    {
        if (hresult < 0) throw new InvalidOperationException($"Media Foundation refused at {step}: 0x{hresult:X8}");
    }

    // The attribute keys are readonly statics; COM wants them by reference, so each call gets a copy.
    private static int Set(IMFAttributes attributes, Guid key, uint value) => attributes.SetUINT32(ref key, value);
    private static int Set(IMFAttributes attributes, Guid key, Guid value) => attributes.SetGUID(ref key, ref value);
    private static int Get32(IMFAttributes attributes, Guid key, out uint value) => attributes.GetUINT32(ref key, out value);
    private static int Get64(IMFAttributes attributes, Guid key, out ulong value) => attributes.GetUINT64(ref key, out value);

    [DllImport("mfplat.dll")] private static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] private static extern int MFShutdown();
    [DllImport("mfplat.dll")] private static extern int MFCreateAttributes(out IMFAttributes attributes, uint initialSize);
    [DllImport("mfplat.dll")] private static extern int MFCreateMediaType(out IMFMediaType type);
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
    private static extern int MFCreateSourceReaderFromURL(string url, IMFAttributes? attributes, out IMFSourceReader reader);

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropVariant
    {
        public ushort Type;
        public ushort Reserved1, Reserved2, Reserved3;
        public long Value;
        public long Padding;
        public static PropVariant Int64(long value) => new() { Type = 20 /* VT_I8 */, Value = value };
    }

    // The Media Foundation interfaces this class touches, vtable order exactly as in mfobjects.h and
    // mfreadwrite.h. Only the methods that are called carry real signatures; the rest keep their slots.
    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [SuppressMessage("Interoperability", "SYSLIB1096", Justification = "Runtime COM interop is what this file is.")]
    internal interface IMFAttributes
    {
        [PreserveSig] int GetItem([In] ref Guid key, IntPtr value);
        [PreserveSig] int GetItemType([In] ref Guid key, out int type);
        [PreserveSig] int CompareItem([In] ref Guid key, IntPtr value, out bool result);
        [PreserveSig] int Compare(IMFAttributes theirs, int matchType, out bool result);
        [PreserveSig] int GetUINT32([In] ref Guid key, out uint value);
        [PreserveSig] int GetUINT64([In] ref Guid key, out ulong value);
        [PreserveSig] int GetDouble([In] ref Guid key, out double value);
        [PreserveSig] int GetGUID([In] ref Guid key, out Guid value);
        [PreserveSig] int GetStringLength([In] ref Guid key, out uint length);
        [PreserveSig] int GetString([In] ref Guid key, IntPtr value, uint size, out uint length);
        [PreserveSig] int GetAllocatedString([In] ref Guid key, out IntPtr value, out uint length);
        [PreserveSig] int GetBlobSize([In] ref Guid key, out uint size);
        [PreserveSig] int GetBlob([In] ref Guid key, IntPtr buffer, uint size, out uint written);
        [PreserveSig] int GetAllocatedBlob([In] ref Guid key, out IntPtr buffer, out uint size);
        [PreserveSig] int GetUnknown([In] ref Guid key, [In] ref Guid iid, out IntPtr value);
        [PreserveSig] int SetItem([In] ref Guid key, IntPtr value);
        [PreserveSig] int DeleteItem([In] ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32([In] ref Guid key, uint value);
        [PreserveSig] int SetUINT64([In] ref Guid key, ulong value);
        [PreserveSig] int SetDouble([In] ref Guid key, double value);
        [PreserveSig] int SetGUID([In] ref Guid key, [In] ref Guid value);
        [PreserveSig] int SetString([In] ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob([In] ref Guid key, IntPtr buffer, uint size);
        [PreserveSig] int SetUnknown([In] ref Guid key, IntPtr value);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
        [PreserveSig] int CopyAllItems(IMFAttributes destination);
    }

    [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [SuppressMessage("Interoperability", "SYSLIB1096", Justification = "Runtime COM interop is what this file is.")]
    internal interface IMFMediaType : IMFAttributes
    {
        [PreserveSig] new int GetItem([In] ref Guid key, IntPtr value);
        [PreserveSig] new int GetItemType([In] ref Guid key, out int type);
        [PreserveSig] new int CompareItem([In] ref Guid key, IntPtr value, out bool result);
        [PreserveSig] new int Compare(IMFAttributes theirs, int matchType, out bool result);
        [PreserveSig] new int GetUINT32([In] ref Guid key, out uint value);
        [PreserveSig] new int GetUINT64([In] ref Guid key, out ulong value);
        [PreserveSig] new int GetDouble([In] ref Guid key, out double value);
        [PreserveSig] new int GetGUID([In] ref Guid key, out Guid value);
        [PreserveSig] new int GetStringLength([In] ref Guid key, out uint length);
        [PreserveSig] new int GetString([In] ref Guid key, IntPtr value, uint size, out uint length);
        [PreserveSig] new int GetAllocatedString([In] ref Guid key, out IntPtr value, out uint length);
        [PreserveSig] new int GetBlobSize([In] ref Guid key, out uint size);
        [PreserveSig] new int GetBlob([In] ref Guid key, IntPtr buffer, uint size, out uint written);
        [PreserveSig] new int GetAllocatedBlob([In] ref Guid key, out IntPtr buffer, out uint size);
        [PreserveSig] new int GetUnknown([In] ref Guid key, [In] ref Guid iid, out IntPtr value);
        [PreserveSig] new int SetItem([In] ref Guid key, IntPtr value);
        [PreserveSig] new int DeleteItem([In] ref Guid key);
        [PreserveSig] new int DeleteAllItems();
        [PreserveSig] new int SetUINT32([In] ref Guid key, uint value);
        [PreserveSig] new int SetUINT64([In] ref Guid key, ulong value);
        [PreserveSig] new int SetDouble([In] ref Guid key, double value);
        [PreserveSig] new int SetGUID([In] ref Guid key, [In] ref Guid value);
        [PreserveSig] new int SetString([In] ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] new int SetBlob([In] ref Guid key, IntPtr buffer, uint size);
        [PreserveSig] new int SetUnknown([In] ref Guid key, IntPtr value);
        [PreserveSig] new int LockStore();
        [PreserveSig] new int UnlockStore();
        [PreserveSig] new int GetCount(out uint count);
        [PreserveSig] new int GetItemByIndex(uint index, out Guid key, IntPtr value);
        [PreserveSig] new int CopyAllItems(IMFAttributes destination);
        [PreserveSig] int GetMajorType(out Guid majorType);
        [PreserveSig] int IsCompressedFormat(out bool compressed);
        [PreserveSig] int IsEqual(IMFMediaType other, out uint flags);
        [PreserveSig] int GetRepresentation(Guid representation, out IntPtr value);
        [PreserveSig] int FreeRepresentation(Guid representation, IntPtr value);
    }

    [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [SuppressMessage("Interoperability", "SYSLIB1096", Justification = "Runtime COM interop is what this file is.")]
    internal interface IMFSample : IMFAttributes
    {
        [PreserveSig] new int GetItem([In] ref Guid key, IntPtr value);
        [PreserveSig] new int GetItemType([In] ref Guid key, out int type);
        [PreserveSig] new int CompareItem([In] ref Guid key, IntPtr value, out bool result);
        [PreserveSig] new int Compare(IMFAttributes theirs, int matchType, out bool result);
        [PreserveSig] new int GetUINT32([In] ref Guid key, out uint value);
        [PreserveSig] new int GetUINT64([In] ref Guid key, out ulong value);
        [PreserveSig] new int GetDouble([In] ref Guid key, out double value);
        [PreserveSig] new int GetGUID([In] ref Guid key, out Guid value);
        [PreserveSig] new int GetStringLength([In] ref Guid key, out uint length);
        [PreserveSig] new int GetString([In] ref Guid key, IntPtr value, uint size, out uint length);
        [PreserveSig] new int GetAllocatedString([In] ref Guid key, out IntPtr value, out uint length);
        [PreserveSig] new int GetBlobSize([In] ref Guid key, out uint size);
        [PreserveSig] new int GetBlob([In] ref Guid key, IntPtr buffer, uint size, out uint written);
        [PreserveSig] new int GetAllocatedBlob([In] ref Guid key, out IntPtr buffer, out uint size);
        [PreserveSig] new int GetUnknown([In] ref Guid key, [In] ref Guid iid, out IntPtr value);
        [PreserveSig] new int SetItem([In] ref Guid key, IntPtr value);
        [PreserveSig] new int DeleteItem([In] ref Guid key);
        [PreserveSig] new int DeleteAllItems();
        [PreserveSig] new int SetUINT32([In] ref Guid key, uint value);
        [PreserveSig] new int SetUINT64([In] ref Guid key, ulong value);
        [PreserveSig] new int SetDouble([In] ref Guid key, double value);
        [PreserveSig] new int SetGUID([In] ref Guid key, [In] ref Guid value);
        [PreserveSig] new int SetString([In] ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] new int SetBlob([In] ref Guid key, IntPtr buffer, uint size);
        [PreserveSig] new int SetUnknown([In] ref Guid key, IntPtr value);
        [PreserveSig] new int LockStore();
        [PreserveSig] new int UnlockStore();
        [PreserveSig] new int GetCount(out uint count);
        [PreserveSig] new int GetItemByIndex(uint index, out Guid key, IntPtr value);
        [PreserveSig] new int CopyAllItems(IMFAttributes destination);
        [PreserveSig] int GetSampleFlags(out uint flags);
        [PreserveSig] int SetSampleFlags(uint flags);
        [PreserveSig] int GetSampleTime(out long time);
        [PreserveSig] int SetSampleTime(long time);
        [PreserveSig] int GetSampleDuration(out long duration);
        [PreserveSig] int SetSampleDuration(long duration);
        [PreserveSig] int GetBufferCount(out uint count);
        [PreserveSig] int GetBufferByIndex(uint index, out IMFMediaBuffer buffer);
        [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
        [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
        [PreserveSig] int RemoveBufferByIndex(uint index);
        [PreserveSig] int RemoveAllBuffers();
        [PreserveSig] int GetTotalLength(out uint length);
        [PreserveSig] int CopyToBuffer(IMFMediaBuffer buffer);
    }

    [ComImport, Guid("045fa593-8799-42b8-bc8d-8968c6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [SuppressMessage("Interoperability", "SYSLIB1096", Justification = "Runtime COM interop is what this file is.")]
    internal interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr buffer, out uint maxLength, out uint currentLength);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out uint length);
        [PreserveSig] int SetCurrentLength(uint length);
        [PreserveSig] int GetMaxLength(out uint length);
    }

    [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [SuppressMessage("Interoperability", "SYSLIB1096", Justification = "Runtime COM interop is what this file is.")]
    internal interface IMFSourceReader
    {
        [PreserveSig] int GetStreamSelection(uint stream, out bool selected);
        [PreserveSig] int SetStreamSelection(uint stream, bool selected);
        [PreserveSig] int GetNativeMediaType(uint stream, uint index, out IMFMediaType type);
        [PreserveSig] int GetCurrentMediaType(uint stream, out IMFMediaType type);
        [PreserveSig] int SetCurrentMediaType(uint stream, IntPtr reserved, IMFMediaType type);
        [PreserveSig] int SetCurrentPosition([In] ref Guid timeFormat, [In] ref PropVariant position);
        [PreserveSig] int ReadSample(uint stream, uint flags, out uint actualStream, out uint streamFlags, out long timestamp, out IMFSample? sample);
        [PreserveSig] int Flush(uint stream);
        [PreserveSig] int GetServiceForStream(uint stream, [In] ref Guid service, [In] ref Guid iid, out IntPtr value);
        [PreserveSig] int GetPresentationAttribute(uint stream, [In] ref Guid attribute, out PropVariant value);
    }
}

/// <summary>The layouts <see cref="MediaFoundationVideo.ReadFrame"/> can deliver.</summary>
public enum VideoPixelFormat
{
    /// <summary>32-bit pixels, blue first, padding byte last, top row first.</summary>
    Bgrx,
    /// <summary>A full-size luma plane followed by an interleaved U/V plane at half width and half height.</summary>
    Nv12,
}
