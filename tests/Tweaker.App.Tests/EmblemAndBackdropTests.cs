using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FluentAssertions;
using Tweaker.App.Effects;
using Tweaker.App.Services;
using Tweaker.App.Views;

namespace Tweaker.App.Tests;

/// <summary>
/// The emblem and the backdrop are the two things on Home that are not text. The emblem plays through
/// Media Foundation — deliberately not through WPF's MediaElement, which needs Windows Media Player —
/// and falls back to a pack of frames where Windows has no codecs. Both paths run here for real.
/// </summary>
[Collection("Wpf")]
public sealed class EmblemAndBackdropTests(WpfRuntime ui)
{
    [Fact]
    public Task TheFramePackShipsInsideTheExecutableAndDecodes() => ui.RunAsync(() =>
    {
        var frames = EmblemFrames.Load();

        frames.Width.Should().Be(800);
        frames.Height.Should().Be(554);
        frames.FramesPerSecond.Should().Be(30);
        frames.Count.Should().BeInRange(140, 165, "five seconds at thirty frames a second");

        var colours = new byte[frames.BytesPerFrame];
        var indices = new byte[frames.ScratchLength];
        frames.Decode(0, colours, indices);
        colours[3].Should().Be(0, "the top-left pixel is background");
        var centre = (frames.Height / 2 * frames.Width + frames.Width / 2) * 4;
        colours[centre + 3].Should().Be(255, "the middle of the emblem is solid");
        (colours[centre] + colours[centre + 1] + colours[centre + 2]).Should().BeGreaterThan(60, "and it is not black");
        for (var index = 1; index < frames.Count; index++) frames.Decode(index, colours, indices);
    });

    [Fact]
    public void ADamagedPackIsRefusedWithAReason()
    {
        var act = () => EmblemFrames.Load(new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15]));

        act.Should().Throw<InvalidDataException>().WithMessage("*emblem frame pack*");
    }

    [Fact]
    public Task TheClipShipsInsideTheExecutableAndComesOutIntact() => ui.RunAsync(() =>
    {
        using var resource = EmblemClipFile.OpenResource();
        resource.Length.Should().BeGreaterThan(400_000, "the clip is a few hundred kilobytes of H.264");

        var directory = Path.Combine(Path.GetTempPath(), "66mods-emblem", Guid.NewGuid().ToString("N"));
        try
        {
            var path = EmblemClipFile.Extract(directory, EmblemClipFile.OpenResource);
            new FileInfo(path).Length.Should().Be(resource.Length);
            var head = new byte[8];
            using (var file = File.OpenRead(path)) file.ReadExactly(head);
            System.Text.Encoding.ASCII.GetString(head, 4, 4).Should().Be("ftyp", "an MP4 starts with an ftyp box");
            EmblemClipFile.Extract(directory, EmblemClipFile.OpenResource).Should().Be(path, "a matching file is reused, not rewritten");

            // Leftovers from earlier builds and interrupted copies are swept on the next launch.
            File.WriteAllBytes(Path.Combine(directory, "emblem.mp4"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(directory, "emblem-12345.mp4.tmp"), [1, 2, 3]);
            EmblemClipFile.Extract(directory, EmblemClipFile.OpenResource).Should().Be(path);
            Directory.GetFiles(directory).Should().BeEquivalentTo([path], "only the current clip stays");

            // The current build's own staging name is left alone: another instance of this build may be
            // copying into it at this very moment.
            File.WriteAllBytes(path + ".tmp", [1, 2, 3]);
            EmblemClipFile.Extract(directory, EmblemClipFile.OpenResource).Should().Be(path);
            File.Exists(path + ".tmp").Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    });

    [Fact]
    public Task MediaFoundationDecodesTheClipToPixelsWithoutWindowsMediaPlayer() => ui.RunAsync(() =>
    {
        // This developer PC has Windows Media Player removed, which is exactly the case that broke MediaElement.
        MediaFoundationVideo.IsAvailable.Should().BeTrue("a non-N Windows has Media Foundation");
        var directory = Path.Combine(Path.GetTempPath(), "66mods-emblem", Guid.NewGuid().ToString("N"));
        try
        {
            using var video = MediaFoundationVideo.Open(EmblemClipFile.Extract(directory, EmblemClipFile.OpenResource));
            video.Width.Should().Be(800);
            video.Height.Should().Be(554);
            video.FramesPerSecond.Should().BeApproximately(30, 0.1);
            video.Format.Should().Be(VideoPixelFormat.Nv12, "the decoder's own planes, no video processor in between");
            video.BytesPerFrame.Should().Be(800 * 554 * 3 / 2);

            var raw = new byte[video.BytesPerFrame];
            var first = video.ReadFrame(raw, out var wrapped);
            wrapped.Should().BeFalse();
            var second = video.ReadFrame(raw, out _);
            second.Should().BeGreaterThan(first, "timestamps advance");

            var keyed = new byte[video.Width * video.Height * 4];
            EmblemView.KeyFrame(video, raw, keyed);
            keyed[3].Should().Be(0, "the corner is background and keyed out");
            var centre = (video.Height / 2 * video.Width + video.Width / 2) * 4;
            keyed[centre + 3].Should().Be(255, "the gem is solid");
            (keyed[centre] + keyed[centre + 1] + keyed[centre + 2]).Should().BeGreaterThan(60, "and it has colour");

            // Read to the end and past it: the reader must wrap to the start on its own.
            var loops = 0;
            for (var frame = 0; frame < 400 && loops == 0; frame++)
                if (video.ReadFrame(raw, out var w) >= 0 && w) loops++;
            loops.Should().Be(1, "the clip is a loop");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    });

    [Fact]
    public Task TheFusedConversionMatchesWindowsOwnColourConversion() => ui.RunAsync(() =>
    {
        // The same first frame two ways: NV12 through KeyNv12, and RGB32 from the reader's video processor.
        // Chroma planes offset by a row, a wrong matrix or a wrong stride all show up here as a big difference.
        var directory = Path.Combine(Path.GetTempPath(), "66mods-emblem", Guid.NewGuid().ToString("N"));
        try
        {
            var path = EmblemClipFile.Extract(directory, EmblemClipFile.OpenResource);
            using var planes = MediaFoundationVideo.Open(path);
            using var pixels = MediaFoundationVideo.Open(path, nativePlanes: false);
            planes.Format.Should().Be(VideoPixelFormat.Nv12);
            pixels.Format.Should().Be(VideoPixelFormat.Bgrx);
            (pixels.Width, pixels.Height).Should().Be((planes.Width, planes.Height));

            var ours = new byte[planes.Width * planes.Height * 4];
            var theirs = new byte[pixels.Width * pixels.Height * 4];
            var raw = new byte[planes.BytesPerFrame];
            var reference = new byte[pixels.BytesPerFrame];
            for (var frame = 0; frame < 40; frame++) { planes.ReadFrame(raw, out _); pixels.ReadFrame(reference, out _); }
            EmblemView.KeyFrame(planes, raw, ours);
            EmblemView.KeyFrame(pixels, reference, theirs);

            long difference = 0, alphaMismatches = 0, opaque = 0;
            for (var i = 0; i < ours.Length; i += 4)
            {
                if (ours[i + 3] == 255 && theirs[i + 3] == 255)
                {
                    opaque++;
                    difference += Math.Abs(ours[i] - theirs[i]) + Math.Abs(ours[i + 1] - theirs[i + 1]) + Math.Abs(ours[i + 2] - theirs[i + 2]);
                }
                else if ((ours[i + 3] == 0) != (theirs[i + 3] == 0)) alphaMismatches++;
            }
            opaque.Should().BeGreaterThan(20_000, "the emblem covers a good part of the frame");
            (difference / (double)(opaque * 3)).Should().BeLessThan(4, "per channel, on opaque pixels");
            (alphaMismatches / (double)(ours.Length / 4)).Should().BeLessThan(0.01, "the two keys agree on what is background");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    });

    [Fact]
    public void TheKeyKeepsTheEmblemAndDropsTheBlack()
    {
        var pixels = new byte[4 * 4];
        BitConverter.GetBytes(0x00000000u).CopyTo(pixels, 0);          // black → gone
        BitConverter.GetBytes(0x000A0A0Au).CopyTo(pixels, 4);          // near-black noise → gone
        BitConverter.GetBytes(0x00C026D3u).CopyTo(pixels, 8);          // magenta → solid
        BitConverter.GetBytes(0x001E1E1Eu).CopyTo(pixels, 12);         // rim → partial, premultiplied
        var keyed = new byte[pixels.Length];

        EmblemView.KeyBackground(pixels, keyed);

        BitConverter.ToUInt32(keyed, 0).Should().Be(0);
        BitConverter.ToUInt32(keyed, 4).Should().Be(0);
        BitConverter.ToUInt32(keyed, 8).Should().Be(0xFFC026D3u);
        var rim = BitConverter.ToUInt32(keyed, 12);
        var alpha = rim >> 24;
        alpha.Should().BeInRange(1, 254);
        (rim & 0xFF).Should().BeLessThanOrEqualTo(alpha, "premultiplied colour never exceeds its alpha");
    }

    [Fact]
    public Task TheAuroraShaderShipsAsPixelShader3Bytecode() => ui.RunAsync(() =>
    {
        var assembly = Uri.EscapeDataString(typeof(AuroraEffect).Assembly.GetName().Name!);
        var info = Application.GetResourceStream(new Uri($"pack://application:,,,/{assembly};component/Effects/Aurora.ps", UriKind.Absolute));

        info.Should().NotBeNull("Aurora.ps must be an application resource");
        using var stream = info!.Stream;
        var head = new byte[4];
        stream.ReadExactly(head);
        BitConverter.ToUInt32(head).Should().Be(0xFFFF0300u, "the aurora needs ps_3_0");
        var effect = new AuroraEffect { Time = 3, Aspect = 1.6 };
        effect.Time.Should().Be(3);
    });

    [Fact]
    public Task UnderReduceMotionTheEmblemIsTheFirstFrameAndNothingIsAnimated() => ui.RunAsync(() =>
    {
        var emblem = new EmblemView { ReduceMotion = true, Width = 400, Height = 300 };
        var window = new Window { Content = emblem, Width = 500, Height = 400, Left = -12000, Top = -12000, ShowInTaskbar = false };
        window.Show();
        try
        {
            Pump(window);
            emblem.State.Should().Be("Still");
            emblem.FrameIndex.Should().Be(0, "the still is the first frame of the same clip");
            emblem.HasAnimatedProperties.Should().BeFalse();
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task WithMotionTheEmblemPlaysTheVideoAndStopsOnReduceMotion() => ui.RunAsync(async () =>
    {
        var emblem = new EmblemView { ReduceMotion = false, Width = 400, Height = 300 };
        var window = new Window { Content = emblem, Width = 500, Height = 400, Left = -12000, Top = -12000, ShowInTaskbar = false };
        window.Show();
        try
        {
            Pump(window);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(6);
            while (emblem.FrameIndex < 3 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
                Pump(window);
            }
            emblem.State.Should().Be("Video", $"this PC has Media Foundation; note: {emblem.VideoNote}");
            emblem.FrameIndex.Should().BeGreaterThanOrEqualTo(3, "frames must keep arriving from the decoder");

            emblem.ReduceMotion = true;
            Pump(window);
            emblem.State.Should().Be("Still");
            emblem.FrameIndex.Should().Be(0, "switching Reduce Motion on returns to the still at once");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task TheVectorBackdropDrawsThreeLightsAndStopsThemUnderReduceMotion() => ui.RunAsync(() =>
    {
        var backdrop = new AuroraBackdrop(useShader: false) { ReduceMotion = true };
        var window = new Window { Content = backdrop, Width = 600, Height = 400, Left = -12000, Top = -12000, ShowInTaskbar = false };
        window.Show();
        try
        {
            Pump(window);
            backdrop.IsShaderBacked.Should().BeFalse();
            var grid = (Grid)System.Windows.Media.VisualTreeHelper.GetChild(backdrop, 0);
            grid.Children.Count.Should().Be(3);
            grid.Children.Cast<UIElement>().Should().OnlyContain(x => x.RenderTransform == null || !x.RenderTransform.HasAnimatedProperties);
        }
        finally
        {
            window.Close();
        }
    });

    private static void Pump(Window window)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
    }
}
