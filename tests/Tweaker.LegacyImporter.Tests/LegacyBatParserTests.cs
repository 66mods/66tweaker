using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Tweaker.Domain.Legacy;
using Tweaker.LegacyImporter;

namespace Tweaker.LegacyImporter.Tests;

public sealed class LegacyBatParserTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly HashSet<string> FrozenFixturePaths = new(StringComparer.Ordinal)
    {
        "66mods Tweaks v40012(RUN AS ADMIN).bat",
        "Fixes/Fix Disabled WiFi (RUN AS ADMIN).bat",
        "Fixes/Fix Fortnite Not Starting (RUN AS ADMIN).bat"
    };

    [Fact]
    public void Parse_TracksSectionLineKindAndSha256()
    {
        const string source = ":power\r\nreg add \"HKCU\\Software\\Demo\" /v Enabled /t REG_DWORD /d 1 /f\r\n";

        var command = new LegacyBatParser().Parse("fixture.bat", source).Single();

        command.Section.Should().Be("power");
        command.LineNumber.Should().Be(2);
        command.Kind.Should().Be(LegacyCommandKind.RegistryAdd);
        command.OriginalText.Should().Be("reg add \"HKCU\\Software\\Demo\" /v Enabled /t REG_DWORD /d 1 /f");
        command.NormalizedText.Should().Be("reg add \"hkcu\\software\\demo\" /v enabled /t reg_dword /d 1 /f");
        command.Fingerprint.Should().Be(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "fixture.bat\n2\nreg add \"hkcu\\software\\demo\" /v enabled /t reg_dword /d 1 /f"))));
    }

    [Fact]
    public void Parse_UsesRegistryVerbInsteadOfCommandData()
    {
        const string source = "reg add HKCU\\Software\\Demo /v Label /d \"delete\" /f\r\nfor /f %%q in ('wmic') do Reg.exe delete HKLM\\Demo /v Enabled /f\r\n";

        var commands = new LegacyBatParser().Parse("fixture.bat", source);

        commands.Select(command => command.Kind).Should().Equal(
            LegacyCommandKind.RegistryAdd,
            LegacyCommandKind.RegistryDelete);
    }

    [Fact]
    public void Parse_OnlyClassifiesObservedPowerShellMutationStructures()
    {
        const string source = "powershell -NoProfile Get-Date\r\nPowerShell Get-Process\r\npowershell -NoProfile Write-Output \"Remove-AppxPackage\"\r\npowershell -Command \"Write-Output 'Disable-NetAdapterLso'\"\r\npowershell -NoProfile # Set-ProcessMitigation\r\npowershell Disable-NetAdapterLso -Name \"*\"\r\npowershell -Command \"Get-AppxPackage *Demo* | Remove-AppxPackage\"\r\npowershell \"ForEach($v in (Get-Command -Name \\\"Set-ProcessMitigation\\\").Parameters){Set-ProcessMitigation -System}\"\r\n";

        var commands = new LegacyBatParser().Parse("fixture.bat", source);

        commands.Should().HaveCount(3);
        commands.Should().OnlyContain(command => command.Kind == LegacyCommandKind.PowerShellMutation);
    }

    [Fact]
    public void Parse_RejectsOversizedInputAndLine()
    {
        var parser = new LegacyBatParser();
        Action oversizedInput = () => parser.Parse("fixture.bat", new string('x', LegacyBatParser.MaximumInputCharacters + 1));
        Action oversizedLine = () => parser.Parse("fixture.bat", new string('x', LegacyBatParser.MaximumLineCharacters + 1));

        oversizedInput.Should().Throw<InvalidDataException>();
        oversizedLine.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Parse_NeverExecutesEmbeddedBatchText()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"legacy-parser-{Guid.NewGuid():N}.txt");
        var source = $"cmd /c echo executed > \"{marker}\"\r\nreg delete HKCU\\Software\\Demo /f\r\n";

        try
        {
            var commands = new LegacyBatParser().Parse("fixture.bat", source);

            commands.Should().ContainSingle().Which.Kind.Should().Be(LegacyCommandKind.RegistryDelete);
            File.Exists(marker).Should().BeFalse();
        }
        finally
        {
            if (File.Exists(marker))
            {
                File.Delete(marker);
            }
        }
    }

    [Fact]
    public void Parse_IsStable()
    {
        const string source = ":network\r\nfor /f %%q in ('wmic') do Reg.exe add HKLM\\Demo /v Enabled /t REG_DWORD /d 1 /f\r\n";
        var parser = new LegacyBatParser();

        var first = parser.Parse("fixture.bat", source);
        var second = parser.Parse("fixture.bat", source);

        second.Should().Equal(first);
    }

    [Fact]
    public void FrozenSources_HaveAuditedMutationCounts()
    {
        var parser = new LegacyBatParser();
        var main = ParseFixture(parser, "66mods Tweaks v40012(RUN AS ADMIN).bat");
        var wifi = ParseFixture(parser, "Fixes/Fix Disabled WiFi (RUN AS ADMIN).bat");
        var fortnite = ParseFixture(parser, "Fixes/Fix Fortnite Not Starting (RUN AS ADMIN).bat");
        var all = main.Concat(wifi).Concat(fortnite).ToArray();

        main.Should().HaveCount(1908);
        wifi.Should().HaveCount(6);
        fortnite.Should().HaveCount(3);
        all.Should().HaveCount(1917);
        all.Select(command => command.NormalizedText).Distinct(StringComparer.Ordinal).Should().HaveCount(1500);
    }

    private static IReadOnlyList<LegacySourceLine> ParseFixture(LegacyBatParser parser, string relativePath)
    {
        return parser.Parse(relativePath, ReadFrozenFixtureText(relativePath));
    }

    private static string ReadFrozenFixtureText(string relativePath)
    {
        if (!FrozenFixturePaths.Contains(relativePath))
        {
            throw new ArgumentException("Only frozen source fixtures may be read.", nameof(relativePath));
        }

        var sourceRoot = Path.GetFullPath(Path.Combine(RepositoryRoot, "legacy", "source"));
        var path = Path.GetFullPath(Path.Combine(sourceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = Path.TrimEndingDirectorySeparator(sourceRoot) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootPrefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Fixture path escapes the frozen source root.");
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4_096, FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.Latin1, detectEncodingFromByteOrderMarks: true, bufferSize: 4_096, leaveOpen: false);
        var builder = new StringBuilder();
        var buffer = new char[4_096];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (builder.Length > LegacyBatParser.MaximumInputCharacters - read)
            {
                throw new InvalidDataException("Frozen fixture exceeds the parser input limit.");
            }

            builder.Append(buffer, 0, read);
        }

        return builder.ToString();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "66mods.Tweaker.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}