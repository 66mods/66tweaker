using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Windows;

namespace Tweaker.App.Services;

/// <summary>
/// The emblem clip as a pack of palette frames, read straight out of the executable.
///
/// Why not a video: WPF's MediaElement needs Windows Media Player, and the people who run a tweaker are
/// exactly the people who have removed Windows Media Player. So the clip ships as its own frames — a
/// 256-colour median-cut palette, the index map and the alpha plane per frame, each zlib-packed — and is
/// expanded one frame at a time into a bitmap the page shows. No codec, no cache folder, nothing written
/// to disk, and the same picture on every edition of Windows.
///
/// Format, little-endian: "66EM", u16 version (2), u16 width, u16 height, u16 fps, u16 count, then per
/// frame u16 palette length, that many RGB triples, u32 packed length + zlib(indices), u32 packed length
/// + zlib(alpha).
/// </summary>
public sealed class EmblemFrames
{
    private const string ResourceName = "Assets/emblem.frames";
    private const int HeaderLength = 14;
    private readonly byte[] data;
    private readonly int[] frameOffsets;

    private EmblemFrames(byte[] data, int width, int height, int fps, int[] frameOffsets)
    {
        this.data = data;
        this.frameOffsets = frameOffsets;
        Width = width;
        Height = height;
        FramesPerSecond = fps;
    }

    public int Width { get; }
    public int Height { get; }
    public int FramesPerSecond { get; }
    public int Count => frameOffsets.Length;
    public int BytesPerFrame => Width * Height * 4;
    /// <summary>Scratch the caller keeps between frames: the index map, then the alpha plane.</summary>
    public int ScratchLength => Width * Height * 2;

    /// <summary>Reads the pack from the application resources.</summary>
    public static EmblemFrames Load()
    {
        var assembly = Uri.EscapeDataString(typeof(EmblemFrames).Assembly.GetName().Name!);
        var uri = new Uri($"pack://application:,,,/{assembly};component/{ResourceName}", UriKind.Absolute);
        using var stream = Application.GetResourceStream(uri)?.Stream
            ?? throw new FileNotFoundException("The emblem frames are missing from the application resources.", ResourceName);
        return Load(stream);
    }

    public static EmblemFrames Load(Stream stream)
    {
        byte[] data;
        if (stream.CanSeek)
        {
            // The resource stream knows its length; one array, no growing copy.
            data = new byte[stream.Length - stream.Position];
            stream.ReadExactly(data);
        }
        else
        {
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            data = buffer.ToArray();
        }
        if (data.Length < HeaderLength || data[0] != (byte)'6' || data[1] != (byte)'6' || data[2] != (byte)'E' || data[3] != (byte)'M')
            throw new InvalidDataException("Not an emblem frame pack.");
        var version = BitConverter.ToUInt16(data, 4);
        if (version != 2) throw new InvalidDataException($"Emblem frame pack version {version} is not supported.");
        int width = BitConverter.ToUInt16(data, 6), height = BitConverter.ToUInt16(data, 8);
        int fps = BitConverter.ToUInt16(data, 10), count = BitConverter.ToUInt16(data, 12);
        if (width == 0 || height == 0 || fps == 0 || count == 0) throw new InvalidDataException("Emblem frame pack header is empty.");

        var offsets = new int[count];
        var position = HeaderLength;
        for (var index = 0; index < count; index++)
        {
            offsets[index] = position;
            var paletteLength = BitConverter.ToUInt16(data, position);
            position += 2 + paletteLength * 3;
            position += 4 + BitConverter.ToInt32(data, position);   // indices
            position += 4 + BitConverter.ToInt32(data, position);   // alpha
            if (position > data.Length) throw new InvalidDataException($"Emblem frame {index} runs past the end of the pack.");
        }
        return new EmblemFrames(data, width, height, fps, offsets);
    }

    /// <summary>
    /// Expands one frame into premultiplied BGRA, the layout a Pbgra32 bitmap takes directly. Both buffers
    /// are the caller's and are reused frame after frame, so playback allocates nothing.
    /// </summary>
    public void Decode(int index, byte[] bgra, byte[] scratch)
    {
        if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
        if (bgra.Length < BytesPerFrame) throw new ArgumentException("The colour buffer is too small for a frame.", nameof(bgra));
        if (scratch.Length < ScratchLength) throw new ArgumentException("The scratch buffer is too small for a frame.", nameof(scratch));

        var pixels = Width * Height;
        var position = frameOffsets[index];
        int paletteLength = BitConverter.ToUInt16(data, position);
        position += 2;
        var palette = new ReadOnlySpan<byte>(data, position, paletteLength * 3);
        position += paletteLength * 3;
        position = Inflate(position, scratch, 0, pixels);
        Inflate(position, scratch, pixels, pixels);

        // Opaque BGRA for every palette entry, once per frame; the pixel loop then only indexes. On the
        // stack, because the worker and the UI thread may both be decoding at the same moment.
        Span<uint> words = stackalloc uint[256];
        words.Clear();
        for (var entry = 0; entry < paletteLength && entry < 256; entry++)
            words[entry] = 0xFF000000u | (uint)palette[entry * 3] << 16 | (uint)palette[entry * 3 + 1] << 8 | palette[entry * 3 + 2];

        var output = MemoryMarshal.Cast<byte, uint>(bgra.AsSpan(0, BytesPerFrame));
        var indices = scratch.AsSpan(0, pixels);
        var alpha = scratch.AsSpan(pixels, pixels);
        for (var pixel = 0; pixel < pixels; pixel++)
        {
            var a = alpha[pixel];
            if (a == 255) { output[pixel] = words[indices[pixel]]; continue; }
            if (a == 0) { output[pixel] = 0; continue; }
            // The antialiased rim, a few hundred pixels a frame: premultiplied by hand.
            var word = words[indices[pixel]];
            uint r = (word >> 16 & 0xFF) * a / 255, g = (word >> 8 & 0xFF) * a / 255, b = (word & 0xFF) * a / 255;
            output[pixel] = (uint)a << 24 | r << 16 | g << 8 | b;
        }
    }

    private int Inflate(int position, byte[] target, int offset, int length)
    {
        var packedLength = BitConverter.ToInt32(data, position);
        position += 4;
        using var packed = new MemoryStream(data, position, packedLength, writable: false);
        using var inflate = new ZLibStream(packed, CompressionMode.Decompress);
        inflate.ReadExactly(target, offset, length);
        return position + packedLength;
    }
}
