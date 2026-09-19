using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Tweaker.App.Effects;

/// <summary>
/// The backdrop shader (Effects/Aurora.hlsl). Drives the whole surface it is applied to; the element's
/// own pixels are ignored, it only has to have some so WPF asks the shader to run.
/// </summary>
public sealed class AuroraEffect : ShaderEffect
{
    private static readonly PixelShader Shader = ShaderLoader.Load("Aurora.ps");

    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty(nameof(Input), typeof(AuroraEffect), 0);

    public static readonly DependencyProperty TimeProperty = DependencyProperty.Register(
        nameof(Time), typeof(double), typeof(AuroraEffect), new UIPropertyMetadata(0.0, PixelShaderConstantCallback(0)));

    public static readonly DependencyProperty AspectProperty = DependencyProperty.Register(
        nameof(Aspect), typeof(double), typeof(AuroraEffect), new UIPropertyMetadata(1.6, PixelShaderConstantCallback(1)));

    public AuroraEffect()
    {
        PixelShader = Shader;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(TimeProperty);
        UpdateShaderValue(AspectProperty);
    }

    public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }
    /// <summary>Seconds. The shader's noise is periodic enough that wrapping this every few minutes is invisible.</summary>
    public double Time { get => (double)GetValue(TimeProperty); set => SetValue(TimeProperty, value); }
    public double Aspect { get => (double)GetValue(AspectProperty); set => SetValue(AspectProperty, value); }

    /// <summary>ps_3_0 only runs on the GPU; anywhere else the caller draws the vector backdrop instead.</summary>
    public static bool IsSupported =>
        RenderCapability.Tier >> 16 >= 2 && RenderCapability.IsPixelShaderVersionSupported(3, 0);
}

internal static class ShaderLoader
{
    /// <summary>
    /// The compiled bytecode ships as an application resource. The assembly name carries a space, so the
    /// pack URI has to be escaped the same way the theme loader in the tests escapes it.
    /// </summary>
    public static PixelShader Load(string fileName)
    {
        var assembly = Uri.EscapeDataString(typeof(ShaderLoader).Assembly.GetName().Name!);
        var shader = new PixelShader
        {
            UriSource = new Uri($"pack://application:,,,/{assembly};component/Effects/{fileName}", UriKind.Absolute)
        };
        shader.Freeze();
        return shader;
    }
}
