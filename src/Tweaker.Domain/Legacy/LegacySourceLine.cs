namespace Tweaker.Domain.Legacy;

public enum LegacyCommandKind
{
    RegistryAdd,
    RegistryDelete,
    PowerCfg,
    BcdEdit,
    ScheduledTask,
    ServiceControl,
    Netsh,
    PowerShellMutation,
    FileDeletion
}

public sealed record LegacySourceLine(
    string SourceFile,
    int LineNumber,
    string Section,
    string OriginalText,
    string NormalizedText,
    LegacyCommandKind Kind,
    string Fingerprint);
