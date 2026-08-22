

namespace Tweaker.Domain.Models;

public enum TweakCategory { Windows, Power, Cpu, Gpu, Network, Input, Privacy, Cleanup, Memory, Games }
public enum ImpactLevel { Low, Medium, High }
public enum RiskLevel { Safe, Advanced, Experimental }
public enum TweakStatus { Pending, Applied, ReadOnlySucceeded, Skipped, Failed, Restored }
public enum TransactionStatus { InProgress, Completed, RolledBack, PartiallyRolledBack }

public sealed record WindowsInfo(string Name, string Version, int Build);
public sealed record CpuInfo(string Name, string Vendor);
public sealed record GpuInfo(string Name, string Vendor, string DriverVersion);
public sealed record MemoryInfo(ulong TotalBytes);
public sealed record PowerInfo(bool IsLaptop, bool IsOnAcPower, string ActivePlan);
public sealed record StorageInfo(string RootPath, string Type, long TotalBytes, long FreeBytes);
public sealed record DetectedGame(string Name, bool Installed, string? ConfigPath)
{
    /// <summary>Set when the scan resolved a concrete install (launcher, executable, config paths).</summary>
    public Games.GameInstallation? Installation { get; init; }
}

public sealed record SystemSnapshot(
    WindowsInfo Windows, CpuInfo Cpu, IReadOnlyList<GpuInfo> Gpus, MemoryInfo Memory, PowerInfo Power,
    IReadOnlyDictionary<string, DetectedGame> Games, IReadOnlyList<string> Warnings)
{
    public IReadOnlyList<StorageInfo> Storage { get; init; } = [];
}

public sealed record TweakDescriptor(string Id, string Name, TweakCategory Category, ImpactLevel Impact,
    RiskLevel Risk, bool RequiresElevation, bool RequiresRestart);
public sealed record TweakRequest(Abstractions.ITweakOperation Operation, string RequestedValue);
public sealed record TweakResult(string OperationId, string? OriginalValue, string RequestedValue, TweakStatus Status,
    bool Verified, string Message, DateTimeOffset Timestamp);
public sealed record TransactionRecord(Guid Id, DateTimeOffset StartedAt, TransactionStatus Status, IReadOnlyList<TweakResult> Results)
{
    public static TransactionRecord Start() => new(Guid.NewGuid(), DateTimeOffset.UtcNow, TransactionStatus.InProgress, []);
    public static TransactionRecord Start(Guid id) => id == Guid.Empty
        ? throw new ArgumentException("A transaction ID is required.", nameof(id))
        : new(id, DateTimeOffset.UtcNow, TransactionStatus.InProgress, []);
}
