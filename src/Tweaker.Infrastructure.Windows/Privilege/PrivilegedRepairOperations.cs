

using System.Text.Json;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;
using Tweaker.Infrastructure.Windows.Operations.Process;
using Tweaker.Infrastructure.Windows.Operations.Services;

namespace Tweaker.Infrastructure.Windows.Privilege;

public static class PrivilegedRepairOperations
{
    public static IReadOnlyList<ITweakOperation> Create(FixedProcessRunner runner) =>
    [
        new ReadOnlyRepairOperation(runner),
        new WifiRepairTransactionOperation(runner)
    ];

    private sealed class ReadOnlyRepairOperation(FixedProcessRunner runner) : IReadOnlyTweakOperation, IRequestedValueProvider
    {
        private bool verified;
        public TweakDescriptor Descriptor { get; } = new("repair.verify-system", "Verify system files",
            TweakCategory.Windows, ImpactLevel.Low, RiskLevel.Safe, true, false);
        public string RequestedValue => "run";
        public bool IsSupported(SystemSnapshot snapshot) => snapshot.Windows.Build >= 17763;
        public Task<string?> ReadCurrentValueAsync(CancellationToken cancellationToken) => Task.FromResult<string?>("read-only-verification");
        public async Task ApplyAsync(string requestedValue, CancellationToken cancellationToken)
        {
            if (!string.Equals(requestedValue, RequestedValue, StringComparison.Ordinal))
                throw new InvalidDataException("The repair catalog value is invalid.");
            var result = await runner.RunAsync(FixedExecutable.Sfc, ["/verifyonly"], TimeSpan.FromMinutes(5), cancellationToken);
            verified = !result.TimedOut && result.ExitCode == 0;
            if (!verified)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.StandardError)
                    ? "System file verification failed." : result.StandardError.Trim());
        }
        public Task<bool> VerifyAsync(string requestedValue, CancellationToken cancellationToken) =>
            Task.FromResult(verified && string.Equals(requestedValue, RequestedValue, StringComparison.Ordinal));
        public Task RestoreAsync(string? originalValue, CancellationToken cancellationToken)
        {
            if (!string.Equals(originalValue, "read-only-verification", StringComparison.Ordinal))
                throw new InvalidDataException("The read-only repair snapshot is invalid.");
            return Task.CompletedTask;
        }
    }

    private sealed class WifiRepairTransactionOperation : ITweakOperation, IRequestedValueProvider
    {
        private readonly ServiceStateOperation[] operations;
        private string[]? inspected;

        public WifiRepairTransactionOperation(FixedProcessRunner runner)
        {
            operations =
            [
                Service(runner, KnownService.Wcmsvc, ServiceStartup.Auto),
                Service(runner, KnownService.WlanSvc, ServiceStartup.Auto),
                Service(runner, KnownService.NativeWifiP, ServiceStartup.Demand)
            ];
        }

        public TweakDescriptor Descriptor { get; } = new("repair.fix-wifi", "Repair legacy-disabled Wi-Fi services",
            TweakCategory.Network, ImpactLevel.Medium, RiskLevel.Safe, true, false);
        public string RequestedValue => "repair-disabled-only";
        public bool IsSupported(SystemSnapshot snapshot) => snapshot.Windows.Build >= 17763;

        public async Task<string?> ReadCurrentValueAsync(CancellationToken cancellationToken)
        {
            inspected = new string[operations.Length];
            for (var index = 0; index < operations.Length; index++)
                inspected[index] = await operations[index].ReadCurrentValueAsync(cancellationToken)
                    ?? throw new InvalidDataException("The Wi-Fi service snapshot is missing.");
            return JsonSerializer.Serialize(inspected);
        }

        public async Task ApplyAsync(string requestedValue, CancellationToken cancellationToken)
        {
            if (!string.Equals(requestedValue, RequestedValue, StringComparison.Ordinal) || inspected is null)
                throw new InvalidDataException("The Wi-Fi repair was not inspected or its catalog value is invalid.");
            try
            {
                for (var index = 0; index < operations.Length; index++)
                    if (IsDisabled(inspected[index]))
                        await operations[index].ApplyAsync(operations[index].RequestedValue, cancellationToken);
            }
            catch
            {
                await RestoreAllAsync(inspected, CancellationToken.None);
                throw;
            }
        }

        public async Task<bool> VerifyAsync(string requestedValue, CancellationToken cancellationToken)
        {
            if (!string.Equals(requestedValue, RequestedValue, StringComparison.Ordinal) || inspected is null) return false;
            for (var index = 0; index < operations.Length; index++)
            {
                if (IsDisabled(inspected[index]))
                {
                    if (!await operations[index].VerifyAsync(operations[index].RequestedValue, cancellationToken)) return false;
                }
                else if (!string.Equals(await operations[index].ReadCurrentValueAsync(cancellationToken), inspected[index], StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        public async Task RestoreAsync(string? originalValue, CancellationToken cancellationToken)
        {
            string[] snapshots;
            try { snapshots = JsonSerializer.Deserialize<string[]>(originalValue ?? string.Empty) ?? []; }
            catch (JsonException error) { throw new InvalidDataException("The Wi-Fi repair snapshot is invalid.", error); }
            if (snapshots.Length != operations.Length) throw new InvalidDataException("The Wi-Fi repair snapshot is invalid.");
            await RestoreAllAsync(snapshots, cancellationToken);
            for (var index = 0; index < operations.Length; index++)
                if (!string.Equals(await operations[index].ReadCurrentValueAsync(cancellationToken), snapshots[index], StringComparison.Ordinal))
                    throw new InvalidOperationException("The Wi-Fi repair rollback did not restore the exact service state.");
        }

        private async Task RestoreAllAsync(IReadOnlyList<string> snapshots, CancellationToken cancellationToken)
        {
            List<Exception>? failures = null;
            for (var index = operations.Length - 1; index >= 0; index--)
            {
                try { await operations[index].RestoreAsync(snapshots[index], cancellationToken); }
                catch (Exception error) { (failures ??= []).Add(error); }
            }
            if (failures is not null) throw new AggregateException("Exact Wi-Fi service rollback was incomplete.", failures);
        }

        private static bool IsDisabled(string snapshot)
        {
            try
            {
                using var document = JsonDocument.Parse(snapshot);
                return document.RootElement.TryGetProperty("Startup", out var startup) && startup.GetInt32() == (int)ServiceStartup.Disabled;
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
            {
                throw new InvalidDataException("The Wi-Fi service snapshot is invalid.", error);
            }
        }

        private static ServiceStateOperation Service(FixedProcessRunner runner, KnownService service, ServiceStartup startup) =>
            new(runner, new($"repair.fix-wifi.{service.ToString().ToLowerInvariant()}", service.ToString(),
                TweakCategory.Network, ImpactLevel.Medium, RiskLevel.Safe, true, false), service, startup, running: true);
    }
}
