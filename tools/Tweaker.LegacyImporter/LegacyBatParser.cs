using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Tweaker.Domain.Legacy;

namespace Tweaker.LegacyImporter;

public sealed partial class LegacyBatParser
{
    public const int MaximumInputCharacters = 1_048_576;
    public const int MaximumLineCharacters = 16_384;

    private static readonly Regex Whitespace = WhitespaceRegex();
    private static readonly Regex RegistryCommand = RegistryCommandRegex();
    private static readonly Regex RegistryForCommand = RegistryForCommandRegex();
    private static readonly Regex PowerCfgCommand = PowerCfgCommandRegex();
    private static readonly Regex BcdEditCommand = BcdEditCommandRegex();
    private static readonly Regex ScheduledTaskCommand = ScheduledTaskCommandRegex();
    private static readonly Regex ServiceCommand = ServiceCommandRegex();
    private static readonly Regex NetshCommand = NetshCommandRegex();
    private static readonly Regex FileDeletionCommand = FileDeletionCommandRegex();

    public IReadOnlyList<LegacySourceLine> Parse(string sourceFile, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFile);
        ArgumentNullException.ThrowIfNull(text);
        ValidateInputBounds(text);

        var result = new List<LegacySourceLine>();
        var section = string.Empty;
        using var reader = new StringReader(text);

        for (var lineNumber = 1; reader.ReadLine() is { } original; lineNumber++)
        {
            var trimmed = original.Trim();
            if (TryGetSection(trimmed, out var label))
            {
                section = label;
                continue;
            }

            if (!TryClassify(trimmed, out var kind))
            {
                continue;
            }

            var normalized = Whitespace.Replace(trimmed, " ").ToLowerInvariant();
            var fingerprint = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes($"{sourceFile}\n{lineNumber}\n{normalized}")));

            result.Add(new LegacySourceLine(sourceFile, lineNumber, section, original, normalized, kind, fingerprint));
        }

        return result;
    }

    private static bool TryGetSection(string value, out string section)
    {
        section = string.Empty;
        if (!value.StartsWith(':') || value.StartsWith("::", StringComparison.Ordinal))
        {
            return false;
        }

        section = value[1..].Trim();
        return section.Length > 0;
    }

    private static bool TryClassify(string value, out LegacyCommandKind kind)
    {
        kind = default;
        if (value.Length == 0 || value.StartsWith("::", StringComparison.Ordinal) ||
            value.StartsWith("rem ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var command = value.TrimStart('@').TrimStart();
        var registryMatch = RegistryCommand.Match(command);
        if (!registryMatch.Success)
        {
            registryMatch = RegistryForCommand.Match(command);
        }

        if (registryMatch.Success)
        {
            kind = registryMatch.Groups["verb"].Value.Equals("delete", StringComparison.OrdinalIgnoreCase)
                ? LegacyCommandKind.RegistryDelete
                : LegacyCommandKind.RegistryAdd;
            return true;
        }

        if (PowerCfgCommand.IsMatch(command))
        {
            kind = LegacyCommandKind.PowerCfg;
            return true;
        }

        if (BcdEditCommand.IsMatch(command))
        {
            kind = LegacyCommandKind.BcdEdit;
            return true;
        }

        if (ScheduledTaskCommand.IsMatch(command))
        {
            kind = LegacyCommandKind.ScheduledTask;
            return true;
        }

        if (ServiceCommand.IsMatch(command))
        {
            kind = LegacyCommandKind.ServiceControl;
            return true;
        }

        if (NetshCommand.IsMatch(command))
        {
            kind = LegacyCommandKind.Netsh;
            return true;
        }

        if (IsObservedPowerShellMutation(command))
        {
            kind = LegacyCommandKind.PowerShellMutation;
            return true;
        }

        if (FileDeletionCommand.IsMatch(command))
        {
            kind = LegacyCommandKind.FileDeletion;
            return true;
        }

        return false;
    }

    private static bool IsObservedPowerShellMutation(string command)
    {
        if (!TryExtractPowerShellScript(command, out var script))
        {
            return false;
        }

        script = script.TrimStart();
        if (StartsWithObservedMutationCmdlet(script))
        {
            return true;
        }

        if (script.StartsWith("ForEach(", StringComparison.OrdinalIgnoreCase))
        {
            var bodyStart = script.IndexOf("){", StringComparison.Ordinal);
            return bodyStart >= 0 && StartsWithObservedMutationCmdlet(script[(bodyStart + 2)..].TrimStart());
        }

        if (!StartsWithCommand(script, "Get-AppxPackage"))
        {
            return false;
        }

        var pipe = script.IndexOf('|');
        return pipe >= 0 && StartsWithCommand(script[(pipe + 1)..].TrimStart(), "Remove-AppxPackage");
    }

    private static bool TryExtractPowerShellScript(string command, out string script)
    {
        script = string.Empty;
        var remainder = command;
        if (remainder.StartsWith("powershell.exe", StringComparison.OrdinalIgnoreCase))
        {
            remainder = remainder["powershell.exe".Length..];
        }
        else if (remainder.StartsWith("powershell", StringComparison.OrdinalIgnoreCase))
        {
            remainder = remainder["powershell".Length..];
        }
        else
        {
            return false;
        }

        if (remainder.Length > 0 && !char.IsWhiteSpace(remainder[0]))
        {
            return false;
        }

        remainder = remainder.TrimStart();
        while (remainder.Length > 0 && remainder[0] == '-')
        {
            var option = ReadPowerShellToken(remainder, out var consumedOption);
            remainder = remainder[consumedOption..].TrimStart();
            if (option.Equals("-NoProfile", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (option.Equals("-ExecutionPolicy", StringComparison.OrdinalIgnoreCase))
            {
                if (remainder.Length == 0)
                {
                    return false;
                }

                _ = ReadPowerShellToken(remainder, out var consumedValue);
                remainder = remainder[consumedValue..].TrimStart();
                continue;
            }

            if (!option.Equals("-Command", StringComparison.OrdinalIgnoreCase) || remainder.Length == 0)
            {
                return false;
            }

            script = ReadPowerShellToken(remainder, out _);
            return script.Length > 0;
        }

        if (remainder.Length == 0)
        {
            return false;
        }

        script = remainder[0] == '"' ? ReadPowerShellToken(remainder, out _) : remainder;
        return script.Length > 0;
    }

    private static string ReadPowerShellToken(string text, out int consumed)
    {
        if (text[0] != '"')
        {
            var whitespace = text.IndexOfAny([' ', '\t']);
            consumed = whitespace < 0 ? text.Length : whitespace;
            return text[..consumed];
        }

        var closingQuote = -1;
        for (var index = 1; index < text.Length; index++)
        {
            if (text[index] == '\\' && index + 1 < text.Length && text[index + 1] == '"')
            {
                index++;
                continue;
            }

            if (text[index] == '"')
            {
                closingQuote = index;
                break;
            }
        }

        if (closingQuote < 0)
        {
            consumed = text.Length;
            return text[1..];
        }

        consumed = closingQuote + 1;
        return text[1..closingQuote];
    }

    private static bool StartsWithObservedMutationCmdlet(string script)
    {
        return StartsWithCommand(script, "Enable-ComputerRestore") ||
               StartsWithCommand(script, "Checkpoint-Computer") ||
               StartsWithCommand(script, "Disable-NetAdapterLso") ||
               StartsWithCommand(script, "Disable-NetAdapterPowerManagement") ||
               StartsWithCommand(script, "Disable-NetAdapterChecksumOffload") ||
               StartsWithCommand(script, "Disable-NetAdapterRsc") ||
               StartsWithCommand(script, "Disable-NetAdapterIPsecOffload") ||
               StartsWithCommand(script, "Disable-NetAdapterQos") ||
               StartsWithCommand(script, "Set-ProcessMitigation") ||
               StartsWithCommand(script, "Remove-Item") ||
               StartsWithCommand(script, "Disable-WindowsOptionalFeature") ||
               StartsWithCommand(script, "Set-SmbClientConfiguration") ||
               StartsWithCommand(script, "Set-SmbServerConfiguration");
    }

    private static bool StartsWithCommand(string script, string command)
    {
        return script.StartsWith(command, StringComparison.OrdinalIgnoreCase) &&
               (script.Length == command.Length || char.IsWhiteSpace(script[command.Length]));
    }

    private static void ValidateInputBounds(string text)
    {
        if (text.Length > MaximumInputCharacters)
        {
            throw new InvalidDataException($"BAT text exceeds the {MaximumInputCharacters}-character limit.");
        }

        var lineLength = 0;
        foreach (var character in text)
        {
            if (character is '\r' or '\n')
            {
                lineLength = 0;
                continue;
            }

            lineLength++;
            if (lineLength > MaximumLineCharacters)
            {
                throw new InvalidDataException($"BAT line exceeds the {MaximumLineCharacters}-character limit.");
            }
        }
    }

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex("^(?:reg(?:\\.exe)?)\\s+(?<verb>add|delete)\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RegistryCommandRegex();

    [GeneratedRegex("^for\\b.*?\\bdo\\s+reg(?:\\.exe)?\\s+(?<verb>add|delete)\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RegistryForCommandRegex();

    [GeneratedRegex("^powercfg\\b.*(?:-(?:setacvalueindex|setdcvalueindex)|[-/]setactive)\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PowerCfgCommandRegex();

    [GeneratedRegex("^bcdedit\\b\\s+/(?:set|delete|deletevalue)\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BcdEditCommandRegex();

    [GeneratedRegex("^schtasks\\b.*\\s/(?:change|create|delete|run)\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ScheduledTaskCommandRegex();

    [GeneratedRegex("^sc(?:\\.exe)?\\s+(?:config|start|stop|delete)\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ServiceCommandRegex();

    [GeneratedRegex("^netsh\\b.*\\b(?:set|add|delete|reset)\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NetshCommandRegex();

    [GeneratedRegex("^(?:del|erase|rd|rmdir)\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FileDeletionCommandRegex();
}