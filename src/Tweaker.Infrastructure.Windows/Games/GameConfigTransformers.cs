using System.Text;
using System.Xml.Linq;
using Tweaker.Domain.Games;

namespace Tweaker.Infrastructure.Windows.Games;

public sealed class UnrealIniTransformer
{
    private static readonly string[] QualityKeys =
    [
        "sg.ViewDistanceQuality", "sg.AntiAliasingQuality", "sg.ShadowQuality", "sg.GlobalIlluminationQuality",
        "sg.ReflectionQuality", "sg.PostProcessQuality", "sg.TextureQuality", "sg.EffectsQuality",
        "sg.FoliageQuality", "sg.ShadingQuality", "sg.LandscapeQuality"
    ];

    public string Transform(string input, string game, GamePerformanceProfile profile)
    {
        var values = Parse(input);
        foreach (var key in QualityKeys) values[key] = profile == GamePerformanceProfile.BalancedFps ? "1" : "0";
        if (game.Equals("Fortnite", StringComparison.OrdinalIgnoreCase))
        {
            values["sg.ResolutionQuality"] = GameProfilePolicy.RenderScale(profile).ToString();
            values["bUseVSync"] = "False";
            values["bUseDynamicResolution"] = "False";
            values["MeshQuality"] = profile == GamePerformanceProfile.BalancedFps ? "1" : "0";
        }
        return Write(input, values);
    }

    private static Dictionary<string, string> Parse(string input)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in input.Replace("\r", "").Split('\n'))
        {
            var separator = line.IndexOf('=');
            if (separator > 0) values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        return values;
    }

    private static string Write(string input, Dictionary<string, string> values)
    {
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var output = new StringBuilder();
        foreach (var line in input.Replace("\r", "").Split('\n'))
        {
            if (line.Length == 0) continue;
            var separator = line.IndexOf('=');
            if (separator > 0)
            {
                var key = line[..separator].Trim();
                if (values.TryGetValue(key, out var value))
                {
                    output.Append(key).Append('=').AppendLine(value); written.Add(key); continue;
                }
            }
            output.AppendLine(line);
        }
        foreach (var pair in values.Where(x => !written.Contains(x.Key) && !GameProfilePolicy.IsProtectedResolutionKey(x.Key)))
            output.Append(pair.Key).Append('=').AppendLine(pair.Value);
        return output.ToString();
    }
}

public sealed class GtaXmlTransformer
{
    public string Transform(string input, GamePerformanceProfile profile)
    {
        var document = XDocument.Parse(input, LoadOptions.PreserveWhitespace);
        Set(document, "ShadowQuality", profile == GamePerformanceProfile.BalancedFps ? "1" : "0");
        Set(document, "TextureQuality", profile == GamePerformanceProfile.BalancedFps ? "1" : "0");
        Set(document, "PopulationDensity", profile == GamePerformanceProfile.BalancedFps ? "0.500000" : "0.000000");
        return document.ToString(SaveOptions.DisableFormatting);
    }
    private static void Set(XDocument document, string element, string value)
    {
        var node = document.Descendants(element).FirstOrDefault();
        node?.SetAttributeValue("value", value);
    }
}

public sealed class MinecraftOptionsTransformer
{
    public string Transform(string input, GamePerformanceProfile profile)
    {
        var distance = profile switch { GamePerformanceProfile.BalancedFps => "10", GamePerformanceProfile.Competitive => "8", GamePerformanceProfile.MegaFps => "6", _ => "4" };
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["renderDistance"] = distance, ["simulationDistance"] = distance, ["graphicsMode"] = "0",
            ["particles"] = "2", ["clouds"] = "false", ["entityDistanceScaling"] = profile == GamePerformanceProfile.UltraPotato ? "0.5" : "0.75"
        };
        var lines = input.Replace("\r", "").Split('\n').Where(x => x.Length > 0).ToList();
        for (var index = 0; index < lines.Count; index++)
        {
            var separator = lines[index].IndexOf(':');
            if (separator <= 0) continue;
            var key = lines[index][..separator];
            if (settings.TryGetValue(key, out var value)) { lines[index] = $"{key}:{value}"; settings.Remove(key); }
        }
        lines.AddRange(settings.Select(x => $"{x.Key}:{x.Value}"));
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }
}

public sealed record RobloxProfilePlan(IReadOnlyDictionary<string, string> AutomatedChanges, IReadOnlyList<string> ManualSteps, IReadOnlyList<string> Warnings);

public sealed class RobloxProfilePlanner
{
    public RobloxProfilePlan Create(GamePerformanceProfile profile) => new(
        new Dictionary<string, string>(),
        ["Open Roblox Settings", "Set Graphics Mode to Manual", profile == GamePerformanceProfile.UltraPotato ? "Set Graphics Quality to 1" : "Set Graphics Quality to 3"],
        ["66mods does not write unofficial FastFlags; Roblox may remove or change them without notice."]);
}

/// <summary>
/// Writes the Roblox client's own graphics settings — <c>GlobalBasicSettings_13.xml</c>, the file every
/// launcher shares because they all run the official client.
/// </summary>
/// <remarks>
/// This is where the frames are. The driver profile can only make the GPU cheaper, and Roblox on a weak PC
/// is held up by the CPU: the quality level drives draw distance, shadow work and lighting quality in one
/// value, and nothing in the NVIDIA layer reaches any of them.
///
/// The file is Roblox's own XML: every setting is an element under <c>Properties</c> carrying a
/// <c>name</c> attribute, with the value as element text. Only elements that already exist are changed —
/// a key this client version does not have is left absent rather than invented, because the client
/// rewrites the file wholesale and an unknown element is not a way to introduce a setting.
///
/// Resolution is not touched. <see cref="GameProfilePolicy.IsProtectedResolutionKey"/> states that rule for
/// the other games and it holds here: <c>StartScreenSize</c> and <c>Fullscreen</c> are the player's, not
/// ours. <c>FramerateCap</c> is also left alone — capping it can raise the floor on a CPU-bound machine,
/// but that is unmeasured and this profile exists to produce the largest number, so it is not ours to
/// lower behind the player's back.
/// </remarks>
public sealed class RobloxSettingsTransformer
{
    /// <summary>
    /// The client's master graphics slider, 1..10. Everything else in this class is worth a fraction of it.
    /// </summary>
    private static int QualityLevel(GamePerformanceProfile profile) => profile switch
    {
        GamePerformanceProfile.BalancedFps => 7,
        GamePerformanceProfile.Competitive => 4,
        GamePerformanceProfile.MegaFps => 2,
        _ => 1
    };

    /// <summary>
    /// Exactly what this profile writes, in order. The preview and the write both read this, so the list
    /// the player is shown before applying cannot drift from the list that is applied.
    /// </summary>
    public static IReadOnlyList<RobloxSettingChange> Plan(GamePerformanceProfile profile)
    {
        var quality = QualityLevel(profile);
        var aggressive = profile is GamePerformanceProfile.MegaFps or GamePerformanceProfile.UltraPotato;
        var changes = new List<RobloxSettingChange>
        {
            new("GraphicsQualityLevel", quality.ToString(), $"Graphics quality level: {quality} of 10"),
            // The slider and the saved level disagree at their peril: the client re-derives one from the other.
            new("SavedQualityLevel", quality.ToString(), $"Saved quality level: {quality}"),
            new("MaxQualityEnabled", "false", "Maximum quality: Off"),

            // Post-processing the client applies regardless of quality level.
            new("VignetteEnabled", aggressive ? "false" : "true", $"Vignette: {(aggressive ? "Off" : "On")}"),
            new("VignetteEnabledCustomOption", aggressive ? "false" : "true",
                $"Vignette (custom): {(aggressive ? "Off" : "On")}"),

            // Instrumentation. The profiler web server is a background listener, not just an overlay.
            new("PerformanceStatsVisible", "false", "Performance stats overlay: Off"),
            new("OnScreenProfilerEnabled", "false", "On-screen profiler: Off"),
            new("MicroProfilerWebServerEnabled", "false", "MicroProfiler web server: Off")
        };

        // Interface. Every nameplate is text layout plus a draw call, which is real on a crowded server —
        // and it is a gameplay trade, so only the profile that exists to make that trade takes it.
        if (profile == GamePerformanceProfile.UltraPotato)
        {
            changes.Add(new("ReducedMotion", "true", "Reduced motion: On"));
            changes.Add(new("PlayerNamesEnabled", "false", "Player nameplates: Off"));
            changes.Add(new("PlayerListVisible", "false", "Player list: Off"));
            changes.Add(new("BadgeVisible", "false", "Badge popups: Off"));
        }
        return changes;
    }

    public string Transform(string input, GamePerformanceProfile profile)
    {
        var document = XDocument.Parse(input, LoadOptions.PreserveWhitespace);
        foreach (var change in Plan(profile)) Set(document, change.Property, change.Value);
        return document.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>
    /// Sets one Roblox property by its <c>name</c> attribute. Absent properties are left absent: the
    /// element type (bool, int, token) is the client's to decide, and guessing it writes a file the client
    /// will reject.
    /// </summary>
    private static void Set(XDocument document, string property, string value)
    {
        var node = document.Descendants()
            .FirstOrDefault(x => (string?)x.Attribute("name") == property);
        if (node is not null) node.Value = value;
    }
}

/// <param name="Display">The line shown in the preview, in the client's own wording where it has one.</param>
public sealed record RobloxSettingChange(string Property, string Value, string Display);
