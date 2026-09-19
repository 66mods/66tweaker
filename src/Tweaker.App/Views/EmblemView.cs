using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Tweaker.App.Services;

namespace Tweaker.App.Views;

/// <summary>
/// The 66mods emblem: the studio's own logo clip, turning over a soft breathing glow.
///
/// The clip is an H.264 file inside the executable, decoded by Windows Media Foundation on a worker
/// thread at its thirty frames a second, its black background keyed out as each frame is copied.
/// That path needs no Windows Media Player. Where Media Foundation itself is missing (an N edition of
/// Windows without the Media Feature Pack) the same clip plays from a pack of palette frames; and the
/// still — the first frame of that pack — is what Reduce Motion shows.
/// </summary>
public sealed class EmblemView : Control
{
    public static readonly DependencyProperty ReduceMotionProperty = DependencyProperty.Register(
        nameof(ReduceMotion), typeof(bool), typeof(EmblemView),
        new PropertyMetadata(false, (d, _) => ((EmblemView)d).SyncPlayback()));

    public bool ReduceMotion { get => (bool)GetValue(ReduceMotionProperty); set => SetValue(ReduceMotionProperty, value); }

    /// <summary>"Still", "Video" (Media Foundation), "Clip" (frame pack), or "Still (…)" with a reason.</summary>
    public string State { get; private set; } = "Still";

    /// <summary>Frames shown so far on the video path, or the pack frame on screen; the tests watch it move.</summary>
    public int FrameIndex { get; private set; } = -1;

    /// <summary>Why the video path was not used on this PC, when it was not; empty otherwise.</summary>
    public string VideoNote { get; private set; } = string.Empty;

    private const int IdleFrameRate = 30;
    private const byte KeyFloor = 18;     // at or below: background
    private const byte KeyCeiling = 44;   // at or above: solid emblem

    private readonly Grid root = new();
    private readonly Ellipse glow;
    private readonly Image image;
    private readonly TranslateTransform bob = new();
    private readonly Stopwatch clock = new();
    private EmblemFrames? frames;
    private WriteableBitmap? bitmap;
    private byte[]? front;
    private byte[]? back;
    private byte[]? indices;
    private byte[]? workerScratch;
    private Task? prefetch;
    private int prefetchedIndex = -1;
    private bool playing;
    private bool rendering;
    private CancellationTokenSource? videoStop;
    private Thread? videoThread;
    private volatile bool presentPending;
    /// <summary>Bumped by every start and stop: a frame from a worker of an earlier playback is not shown.</summary>
    private int generation;
    private Window? host;

    public EmblemView()
    {
        IsHitTestVisible = false;
        glow = new Ellipse
        {
            Stretch = Stretch.Fill,
            Opacity = 0.75,
            RenderTransformOrigin = new Point(0.5, 0.55),
            RenderTransform = new ScaleTransform(1, 1),
            Fill = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.48, 0.55),
                Center = new Point(0.5, 0.58),
                RadiusX = 0.5,
                RadiusY = 0.46,
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x50, 0xC0, 0x26, 0xD3), 0),
                    new GradientStop(Color.FromArgb(0x2C, 0x8B, 0x5C, 0xF6), 0.5),
                    new GradientStop(Color.FromArgb(0x00, 0x8B, 0x5C, 0xF6), 1)
                }
            }
        };
        image = new Image { Stretch = Stretch.None };
        // Linear, not HighQuality: WPF runs the Fant filter on the CPU every time a bitmap changes, and this
        // one changes many times a second. Shown at its own size wherever there is room (no resampling at
        // all), and only ever scaled down, never up, so the pixel art stays as crisp as the source.
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Linear);
        var fit = new Viewbox { Child = image, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };

        var stage = new Grid { RenderTransform = bob, Margin = new Thickness(6) };
        stage.Children.Add(fit);
        root.Children.Add(glow);
        root.Children.Add(stage);
        AddVisualChild(root);
        AddLogicalChild(root);

        Loaded += (_, _) => { WatchHost(); SyncPlayback(); };
        Unloaded += (_, _) => { UnwatchHost(); StopPlayback(); };
    }

    // A minimised window still runs its dispatcher, so without this the worker would go on decoding and
    // keying thirty frames a second into a window nobody sees, which is the app's usual idle state.
    private void WatchHost()
    {
        host = Window.GetWindow(this);
        if (host is not null) host.StateChanged += Host_OnStateChanged;
    }

    private void UnwatchHost()
    {
        if (host is not null) host.StateChanged -= Host_OnStateChanged;
        host = null;
    }

    private void Host_OnStateChanged(object? sender, EventArgs e)
    {
        if (host?.WindowState == WindowState.Minimized) StopPlayback();
        else SyncPlayback();
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => root;

    protected override Size MeasureOverride(Size constraint)
    {
        root.Measure(constraint);
        return root.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        root.Arrange(new Rect(finalSize));
        return finalSize;
    }

    private void SyncPlayback()
    {
        if (!IsLoaded) return;
        if (!EnsureFrames()) return;
        if (ReduceMotion || host?.WindowState == WindowState.Minimized) StopPlayback();
        else StartPlayback();
    }

    /// <summary>Loads the frame pack once and shows its first frame, whichever way the switch is set.</summary>
    private bool EnsureFrames()
    {
        if (frames is not null) return true;
        try
        {
            frames = EmblemFrames.Load();
            bitmap = new WriteableBitmap(frames.Width, frames.Height, 96, 96, PixelFormats.Pbgra32, null);
            front = new byte[frames.BytesPerFrame];
            back = new byte[frames.BytesPerFrame];
            indices = new byte[frames.ScratchLength];
            workerScratch = new byte[frames.ScratchLength];
            image.Source = bitmap;
            image.Width = frames.Width;
            image.Height = frames.Height;
            frames.Decode(0, front, indices);
            Present(front, 0);
            return true;
        }
        catch (Exception error)
        {
            // A damaged resource leaves the glow and an empty stage; the reason is kept for the tests.
            State = $"Still (no clip: {error.Message})";
            return false;
        }
    }

    private void StartPlayback()
    {
        if (playing) return;
        playing = true;
        Breathe(true);
        // A note means the video path already refused once on this PC; it is not tried again.
        if (VideoNote.Length == 0 && !MediaFoundationVideo.IsAvailable) VideoNote = "Media Foundation is not installed on this Windows.";
        if (VideoNote.Length > 0)
        {
            StartFrames();
            return;
        }
        State = "Video";
        videoStop = new CancellationTokenSource();
        var token = videoStop.Token;
        var mine = ++generation;
        videoThread = new Thread(() => PlayVideo(token, mine)) { IsBackground = true, Name = "66mods emblem", Priority = ThreadPriority.BelowNormal };
        videoThread.Start();
    }

    private void StopPlayback()
    {
        playing = false;
        generation++;
        Breathe(false);
        // Cancelled, not joined: the worker may be inside Media Foundation or a file copy that takes a
        // moment, and the UI thread has no reason to wait for it. The generation keeps its late frames out.
        videoStop?.Cancel();
        videoThread = null;
        videoStop = null;
        presentPending = false;
        StopFrames();
        if (frames is null || front is null || indices is null) return;
        frames.Decode(0, front, indices);
        Present(front, 0);
        State = "Still";
    }

    // ---- video path -------------------------------------------------------------------------------

    /// <summary>
    /// The worker: decode a frame, key its background, wait for its moment, hand it to the UI thread.
    /// Two keyed buffers alternate; a frame whose predecessor has not been shown yet is dropped rather
    /// than queued, so a busy UI thread never builds a backlog.
    /// </summary>
    private void PlayVideo(CancellationToken token, int mine)
    {
        try
        {
            var path = EmblemClipFile.Extract();
            token.ThrowIfCancellationRequested();
            using var video = MediaFoundationVideo.Open(path);
            token.ThrowIfCancellationRequested();
            if (frames is null || video.Width != frames.Width || video.Height != frames.Height)
                throw new InvalidOperationException($"The clip is {video.Width}x{video.Height}; the still is {frames?.Width}x{frames?.Height}.");

            var raw = new byte[video.BytesPerFrame];
            var pixelBytes = video.Width * video.Height * 4;
            var keyed = new[] { new byte[pixelBytes], new byte[pixelBytes] };
            var slot = 0;
            var pace = Stopwatch.StartNew();
            long origin = -1;

            while (!token.IsCancellationRequested)
            {
                var timestamp = video.ReadFrame(raw, out var wrapped);
                if (wrapped || origin < 0)
                {
                    origin = timestamp;
                    pace.Restart();
                }
                var due = TimeSpan.FromTicks(timestamp - origin);   // Media Foundation counts in 100 ns, like TimeSpan
                var wait = due - pace.Elapsed;
                if (wait > TimeSpan.FromMilliseconds(1) && token.WaitHandle.WaitOne(wait)) break;
                if (presentPending) continue;   // the window is behind: drop this frame rather than queue it

                KeyFrame(video, raw, keyed[slot]);
                var buffer = keyed[slot];
                presentPending = true;
                Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
                {
                    if (playing && generation == mine) Present(buffer, FrameIndex + 1);
                    presentPending = false;
                });
                slot ^= 1;
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Not this PC's clip, then: fall back to the frame pack, and say why for the tests and the log.
            VideoNote = error.Message;
            Dispatcher.BeginInvoke(() => { if (playing && generation == mine) StartFrames(); });
        }
    }

    /// <summary>Turns one decoded frame, whatever its layout, into premultiplied BGRA with the background keyed out.</summary>
    internal static void KeyFrame(MediaFoundationVideo video, byte[] frame, byte[] premultiplied)
    {
        if (video.Format == VideoPixelFormat.Nv12) KeyNv12(frame, video.Width, video.Height, premultiplied);
        else KeyBackground(frame, premultiplied);
    }

    internal static void KeyBackground(byte[] bgrx, byte[] premultiplied)
    {
        var source = MemoryMarshal.Cast<byte, uint>(bgrx);
        var target = MemoryMarshal.Cast<byte, uint>(premultiplied);
        for (var i = 0; i < source.Length; i++)
        {
            var pixel = source[i];
            target[i] = Key(pixel >> 16 & 0xFF, pixel >> 8 & 0xFF, pixel & 0xFF);
        }
    }

    /// <summary>
    /// NV12 straight to keyed pixels in one pass: BT.709 limited range, the matrix the clip is tagged with,
    /// in 8.8 fixed point. One chroma sample serves a 2×2 block, so the per-block terms are computed once.
    /// </summary>
    internal static void KeyNv12(byte[] nv12, int width, int height, byte[] premultiplied)
    {
        if ((width & 1) != 0 || (height & 1) != 0) throw new ArgumentException("NV12 needs even dimensions.");
        var luma = nv12.AsSpan(0, width * height);
        var chroma = nv12.AsSpan(width * height, width * height / 2);
        var target = MemoryMarshal.Cast<byte, uint>(premultiplied.AsSpan(0, width * height * 4));
        for (var y = 0; y < height; y++)
        {
            var lumaRow = luma.Slice(y * width, width);
            var chromaRow = chroma.Slice(y / 2 * width, width);
            var targetRow = target.Slice(y * width, width);
            for (var x = 0; x < width; x += 2)
            {
                int u = chromaRow[x] - 128, v = chromaRow[x + 1] - 128;
                int red = 459 * v + 128, green = -55 * u - 136 * v + 128, blue = 541 * u + 128;
                targetRow[x] = Key(lumaRow[x], red, green, blue);
                targetRow[x + 1] = Key(lumaRow[x + 1], red, green, blue);
            }
        }
    }

    private static uint Key(int luma, int red, int green, int blue)
    {
        var scaled = 298 * (luma - 16);
        return Key(Clamp(scaled + red >> 8), Clamp(scaled + green >> 8), Clamp(scaled + blue >> 8));
    }

    private static uint Clamp(int value) => (uint)Math.Clamp(value, 0, 255);

    /// <summary>The key itself: black and near-black vanish, a short ramp softens the rim, everything else is opaque.</summary>
    private static uint Key(uint r, uint g, uint b)
    {
        var brightest = Math.Max(r, Math.Max(g, b));
        if (brightest <= KeyFloor) return 0;
        if (brightest >= KeyCeiling) return 0xFF000000u | r << 16 | g << 8 | b;
        var alpha = (brightest - KeyFloor) * 255 / (KeyCeiling - KeyFloor);
        return alpha << 24 | Math.Min(r, alpha) << 16 | Math.Min(g, alpha) << 8 | Math.Min(b, alpha);
    }

    // ---- frame-pack path (fallback) ---------------------------------------------------------------

    private void StartFrames()
    {
        if (frames is null) return;
        State = "Clip";
        clock.Restart();
        Prefetch(1);
        if (!rendering)
        {
            rendering = true;
            CompositionTarget.Rendering += OnRendering;
        }
    }

    private void StopFrames()
    {
        if (rendering)
        {
            rendering = false;
            CompositionTarget.Rendering -= OnRendering;
        }
        clock.Reset();
    }

    /// <summary>The render clock decides which frame is due; a prepared buffer is copied, a missed one decoded.</summary>
    private void OnRendering(object? sender, EventArgs e)
    {
        if (!playing || frames is null || front is null || back is null || indices is null) return;
        var due = (int)(clock.Elapsed.TotalSeconds * frames.FramesPerSecond) % frames.Count;
        if (due == FrameIndex) return;
        if (prefetch is { IsCompleted: false }) return;   // the next frame is a moment away; hold this one

        if (prefetchedIndex == due && prefetch is { IsCompletedSuccessfully: true })
            (front, back) = (back, front);
        else
            frames.Decode(due, front, indices);   // the clock skipped ahead of the worker
        Present(front, due);
        Prefetch((due + 1) % frames.Count);
    }

    private void Prefetch(int index)
    {
        if (frames is null || back is null || workerScratch is null) return;
        var target = back;
        var scratch = workerScratch;
        var pack = frames;
        prefetchedIndex = index;
        // Its own scratch buffer: the UI thread may be decoding a missed frame into the shared one.
        prefetch = Task.Run(() => pack.Decode(index, target, scratch));
    }

    private void Present(byte[] pixels, int index)
    {
        if (frames is null || bitmap is null) return;
        bitmap.WritePixels(new Int32Rect(0, 0, frames.Width, frames.Height), pixels, frames.Width * 4, 0);
        FrameIndex = index;
    }

    /// <summary>The glow swells and the emblem lifts a little, both over several seconds. Never under Reduce Motion.</summary>
    private void Breathe(bool on)
    {
        var scale = (ScaleTransform)glow.RenderTransform;
        if (!on)
        {
            glow.BeginAnimation(OpacityProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            bob.BeginAnimation(TranslateTransform.YProperty, null);
            glow.Opacity = 0.75;
            scale.ScaleX = scale.ScaleY = 1;
            bob.Y = 0;
            return;
        }
        // These three run for as long as Home is open, so they are paced: a 60 Hz animation would make the
        // window compose sixty frames a second for a glow that moves a pixel a second.
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        var breathe = new DoubleAnimation(0.7, 1.0, TimeSpan.FromSeconds(3.5))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease
        };
        var swell = new DoubleAnimation(1.0, 1.04, TimeSpan.FromSeconds(3.5))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease
        };
        var lift = new DoubleAnimation(0, -8, TimeSpan.FromSeconds(3))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease
        };
        foreach (var animation in new[] { breathe, swell, lift }) Timeline.SetDesiredFrameRate(animation, IdleFrameRate);
        glow.BeginAnimation(OpacityProperty, breathe);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, swell);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, swell);
        bob.BeginAnimation(TranslateTransform.YProperty, lift);
    }
}
