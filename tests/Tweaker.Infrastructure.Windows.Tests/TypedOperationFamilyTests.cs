
using System.Diagnostics;
using System.Text;
using FluentAssertions;
using RegistryValueKind = Microsoft.Win32.RegistryValueKind;
using Tweaker.Domain.Models;
using Tweaker.Infrastructure.Windows.Operations.Boot;
using Tweaker.Infrastructure.Windows.Operations.Network;
using Tweaker.Infrastructure.Windows.Operations.Packages;
using Tweaker.Infrastructure.Windows.Operations.Power;
using Tweaker.Infrastructure.Windows.Operations.Process;
using Tweaker.Infrastructure.Windows.Operations.Registry;
using Tweaker.Infrastructure.Windows.Operations.Services;
using Tweaker.Infrastructure.Windows.Operations.Tasks;
using Tweaker.Infrastructure.Windows.Registry;

namespace Tweaker.Infrastructure.Windows.Tests;

public sealed class TypedOperationFamilyTests
{
    public static IEnumerable<object[]> RegistryValues()
    {
        yield return [RegistryValueKind.DWord, 7, 9];
        yield return [RegistryValueKind.QWord, 7L, 9L];
        yield return [RegistryValueKind.String, "before", "after"];
        yield return [RegistryValueKind.ExpandString, "%SystemRoot%", "%TEMP%"];
        yield return [RegistryValueKind.MultiString, new[] { "one", "two" }, new[] { "three", "four" }];
        yield return [RegistryValueKind.Binary, new byte[] { 1, 2, 3 }, new byte[] { 4, 5 }];
    }

    [Theory]
    [MemberData(nameof(RegistryValues))]
    public async Task TypedRegistryOperation_RestoresEverySupportedRegistryKind(RegistryValueKind kind, object original, object requested)
    {
        var store = new MemoryRegistry(new(true, kind, Clone(original)));
        var target = RegistryTarget.CurrentUserWrite(@"Software\66mods", "Value", kind, Clone(requested));
        var operation = new TypedRegistryOperation(store, Descriptor("registry.round-trip"), target);

        var snapshot = await operation.ReadCurrentValueAsync(CancellationToken.None);
        await operation.ApplyAsync(operation.RequestedValue, CancellationToken.None);
        (await operation.VerifyAsync(operation.RequestedValue, CancellationToken.None)).Should().BeTrue();
        await operation.RestoreAsync(snapshot, CancellationToken.None);

        store.Value.Kind.Should().Be(kind);
        Equivalent(store.Value.Value, original).Should().BeTrue();
    }

    [Fact]
    public async Task TypedRegistryOperation_RestoresMissingValueByDeletingIt()
    {
        var store = new MemoryRegistry(RegistryRawValue.Missing);
        var target = RegistryTarget.CurrentUserWrite("Software", "Value", RegistryValueKind.DWord, 1);
        var operation = new TypedRegistryOperation(store, Descriptor("registry.missing"), target);

        var snapshot = await operation.ReadCurrentValueAsync(CancellationToken.None);
        await operation.ApplyAsync(operation.RequestedValue, CancellationToken.None);
        await operation.RestoreAsync(snapshot, CancellationToken.None);

        store.Value.Should().Be(RegistryRawValue.Missing);
    }

    [Theory]
    [InlineData(RegistryValueKind.DWord, "1")]
    [InlineData(RegistryValueKind.String, "bad\0value")]
    public void RegistryTarget_RejectsTypeMismatchAndNul(RegistryValueKind kind, string invalid) =>
        ((Action)(() => RegistryTarget.CurrentUserWrite("Software", "Value", kind, invalid))).Should().Throw<InvalidDataException>();

    [Fact]
    public void RegistrySnapshot_RejectsPayloadThatDoesNotMatchItsKind()
    {
        var invalid = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"Hive\":0,\"SubKey\":\"Software\",\"ValueName\":\"Value\",\"Exists\":true,\"Kind\":4,\"Payload\":\"s:bm90LWFuLWludGVnZXI=\"}"));

        Action decode = () => RegistrySnapshot.Decode(invalid);

        decode.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public async Task Runner_UsesLiteralSystem32ExecutableArgumentListAndNoShell()
    {
        var executor = new DelegateExecutor((start, _, _) => Task.FromResult(Success()));
        var runner = new FixedProcessRunner(null, executor);

        await runner.RunAsync(FixedExecutable.PowerCfg, ["/getactivescheme"], CancellationToken.None);

        executor.Starts.Single().FileName.Should().Be(Path.Combine(Environment.SystemDirectory, "powercfg.exe"));
        executor.Starts.Single().UseShellExecute.Should().BeFalse();
        executor.Starts.Single().ArgumentList.Should().Equal("/getactivescheme");
    }

    [Fact]
    public async Task Runner_SfcResolutionIgnoresCurrentDirectoryAndUsesAbsoluteSystem32Image()
    {
        var executor = new DelegateExecutor((start, _, _) => Task.FromResult(Success()));
        var runner = new FixedProcessRunner(null, executor);
        var original = Environment.CurrentDirectory;
        var planted = Path.Combine(Path.GetTempPath(), "66mods-planted-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(planted);
        try
        {
            Environment.CurrentDirectory = planted;
            await runner.RunAsync(FixedExecutable.Sfc, ["/verifyonly"], CancellationToken.None);
        }
        finally
        {
            Environment.CurrentDirectory = original;
            Directory.Delete(planted);
        }

        executor.Starts.Single().FileName.Should().Be(Path.Combine(Environment.SystemDirectory, "sfc.exe"));
        executor.Starts.Single().WorkingDirectory.Should().Be(Environment.SystemDirectory);
        executor.Starts.Single().UseShellExecute.Should().BeFalse();
        executor.Starts.Single().ArgumentList.Should().Equal("/verifyonly");
    }

    [Fact]
    public async Task Runner_RejectsExecutorOutputBeyondTheBound()
    {
        var runner = new FixedProcessRunner(null, new DelegateExecutor((_, _, _) => Task.FromResult(new FixedProcessResult(0, new string('x', 64 * 1024 + 1), string.Empty, false))));

        await Assert.ThrowsAsync<InvalidDataException>(() => runner.RunAsync(FixedExecutable.Sc, ["query", "Wcmsvc"], CancellationToken.None));
    }

    [Fact]
    public async Task Runner_PreservesAReportedTimeout()
    {
        var runner = new FixedProcessRunner(null, new DelegateExecutor((_, _, _) => Task.FromResult(new FixedProcessResult(-1, string.Empty, "The fixed process timed out.", true))));

        var result = await runner.RunAsync(FixedExecutable.Netsh, ["interface", "tcp"], CancellationToken.None);

        result.TimedOut.Should().BeTrue();
    }
    [Fact]
    public async Task Runner_ForwardsTimeoutAndCancellationToTheFixedExecutor()
    {
        var timeout = TimeSpan.FromSeconds(2);
        var executor = new DelegateExecutor((_, actualTimeout, token) =>
        {
            actualTimeout.Should().Be(timeout);
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new FixedProcessResult(-1, string.Empty, "The fixed process timed out.", true));
        });
        var runner = new FixedProcessRunner(timeout, executor);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(FixedExecutable.Netsh, ["interface", "tcp"], cancellation.Token));
    }

    [Fact]
    public async Task Service_RestoresBootStartupAndRunningStateExactly()
    {
        var service = new ServiceMachine(ServiceStartup.Boot, running: true);
        var operation = new ServiceStateOperation(RunnerFor(service.ExecuteAsync), Descriptor("service"), KnownService.Wcmsvc, ServiceStartup.Disabled, running: false);

        var snapshot = await operation.ReadCurrentValueAsync(CancellationToken.None);
        await operation.ApplyAsync(operation.RequestedValue, CancellationToken.None);
        await operation.RestoreAsync(snapshot, CancellationToken.None);

        service.Startup.Should().Be(ServiceStartup.Boot);
        service.Running.Should().BeTrue();
    }

    [Fact]
    public async Task Service_RejectsMissingServiceInsteadOfJournalingAState()
    {
        var service = new ServiceMachine(ServiceStartup.Auto, true) { Missing = true };
        var operation = new ServiceStateOperation(RunnerFor(service.ExecuteAsync), Descriptor("service.missing"), KnownService.Wcmsvc, ServiceStartup.Disabled, false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => operation.ReadCurrentValueAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Service_CompensatesAFailedStop()
    {
        var service = new ServiceMachine(ServiceStartup.Auto, running: true) { FailStop = true };
        var operation = new ServiceStateOperation(RunnerFor(service.ExecuteAsync), Descriptor("service.compensation"), KnownService.Wcmsvc, ServiceStartup.Disabled, false);

        await operation.ReadCurrentValueAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => operation.ApplyAsync(operation.RequestedValue, CancellationToken.None));

        service.Startup.Should().Be(ServiceStartup.Auto);
        service.Running.Should().BeTrue();
    }

    [Fact]
    public async Task ScheduledTask_RefusesMissingAndAccessDeniedBeforeJournaling()
    {
        var missing = new TaskMachine { Missing = true };
        var missingOperation = new ScheduledTaskStateOperation(RunnerFor(missing.ExecuteAsync), Descriptor("task.missing"), KnownScheduledTask.MicrosoftEdgeUpdate, false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => missingOperation.ReadCurrentValueAsync(CancellationToken.None));

        var denied = new TaskMachine { Denied = true };
        var deniedOperation = new ScheduledTaskStateOperation(RunnerFor(denied.ExecuteAsync), Descriptor("task.denied"), KnownScheduledTask.MicrosoftEdgeUpdate, false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => deniedOperation.ReadCurrentValueAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ScheduledTask_RestoresItsExactEnabledState()
    {
        var task = new TaskMachine { Enabled = true };
        var operation = new ScheduledTaskStateOperation(RunnerFor(task.ExecuteAsync), Descriptor("task"), KnownScheduledTask.MicrosoftEdgeUpdate, false);

        var snapshot = await operation.ReadCurrentValueAsync(CancellationToken.None);
        await operation.ApplyAsync(operation.RequestedValue, CancellationToken.None);
        await operation.RestoreAsync(snapshot, CancellationToken.None);

        task.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task PowerSetting_PersistedSnapshotRestoresWithFreshOperationInstance()
    {
        var power = new PowerMachine { Ac = 25, Dc = 15 };
        var descriptor = Descriptor("power");
        var instanceA = new PowerSettingOperation(RunnerFor(power.ExecuteAsync), descriptor, KnownPowerSetting.ProcessorMinimum, 5, 3);
        var snapshot = await instanceA.ReadCurrentValueAsync(CancellationToken.None) ?? throw new InvalidOperationException("Snapshot missing.");
        await instanceA.ApplyAsync(instanceA.RequestedValue, CancellationToken.None);
        (await instanceA.VerifyAsync(instanceA.RequestedValue, CancellationToken.None)).Should().BeTrue();

        var instanceB = new PowerSettingOperation(RunnerFor(power.ExecuteAsync), descriptor, KnownPowerSetting.ProcessorMinimum, 5, 3);
        await instanceB.RestoreAsync(snapshot, CancellationToken.None);
        var fresh = await instanceB.ReadCurrentValueAsync(CancellationToken.None);

        power.Ac.Should().Be(25);
        power.Dc.Should().Be(15);
        fresh.Should().Contain("\"Ac\":25").And.Contain("\"Dc\":15");
    }
    [Fact]
    public void PowerSetting_RejectsValuesOutsideCompiledRanges()
    {
        var runner = RunnerFor(new PowerMachine().ExecuteAsync);

        ((Action)(() => new PowerSettingOperation(runner, Descriptor("power.minimum"), KnownPowerSetting.ProcessorMinimum, -1, 1))).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => new PowerSettingOperation(runner, Descriptor("power.cooling"), KnownPowerSetting.CoolingPolicy, 0, 2))).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task PowerSetting_FreshInstanceRejectsCorruptSchemaSettingValueAndGuidSnapshots()
    {
        var power = new PowerMachine { Ac = 25, Dc = 15 };
        var descriptor = Descriptor("power.snapshot");
        var instanceA = new PowerSettingOperation(RunnerFor(power.ExecuteAsync), descriptor, KnownPowerSetting.ProcessorMinimum, 5, 3);
        var snapshot = await instanceA.ReadCurrentValueAsync(CancellationToken.None) ?? throw new InvalidOperationException("Snapshot missing.");
        var instanceB = new PowerSettingOperation(RunnerFor(power.ExecuteAsync), descriptor, KnownPowerSetting.ProcessorMinimum, 5, 3);
        var foreignGuid = System.Text.RegularExpressions.Regex.Replace(snapshot, """(?<="Scheme":")[^"]+""", "22222222-2222-2222-2222-222222222222");
        var corruptSchema = System.Text.RegularExpressions.Regex.Replace(snapshot, """(?<="Schema":)\d+""", "2");
        var foreignSetting = System.Text.RegularExpressions.Regex.Replace(snapshot, """(?<="Setting":)\d+""", "1");
        var corruptValue = System.Text.RegularExpressions.Regex.Replace(snapshot, """(?<="Ac":)-?\d+""", "-1");

        await Assert.ThrowsAsync<InvalidDataException>(() => instanceB.RestoreAsync(foreignGuid, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => instanceB.RestoreAsync(corruptSchema, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => instanceB.RestoreAsync(foreignSetting, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => instanceB.RestoreAsync(corruptValue, CancellationToken.None));

        power.SetCalls.Should().Be(0);
    }
    [Fact]
    public async Task PowerSetting_RefusesToMutateWhenTheInspectedPlanChanged()
    {
        var power = new PowerMachine();
        var operation = new PowerSettingOperation(RunnerFor(power.ExecuteAsync), Descriptor("power.changed"), KnownPowerSetting.ProcessorMinimum, 5, 3);

        await operation.ReadCurrentValueAsync(CancellationToken.None);
        power.Ac = 99;
        await Assert.ThrowsAsync<InvalidOperationException>(() => operation.ApplyAsync(operation.RequestedValue, CancellationToken.None));
        power.SetCalls.Should().Be(0);
    }

    [Fact]
    public async Task PowerSetting_CompensatesTheAcWriteWhenDcWriteFails()
    {
        var power = new PowerMachine { Ac = 25, Dc = 15, FailDcWrite = true };
        var operation = new PowerSettingOperation(RunnerFor(power.ExecuteAsync), Descriptor("power.compensation"), KnownPowerSetting.ProcessorMinimum, 5, 3);

        await operation.ReadCurrentValueAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => operation.ApplyAsync(operation.RequestedValue, CancellationToken.None));

        power.Ac.Should().Be(25);
        power.Dc.Should().Be(15);
    }

    [Fact]
    public async Task Netsh_GlobalOperationRestoresEveryKnownAutotuningState()
    {
        var network = new NetworkMachine { Level = TcpAutotuningLevel.Restricted };
        var operation = new NetshSettingOperation(RunnerFor(network.ExecuteAsync), Descriptor("network"), KnownNetshSetting.GlobalAutotuning, TcpAutotuningLevel.Disabled);

        var snapshot = await operation.ReadCurrentValueAsync(CancellationToken.None);
        await operation.ApplyAsync(operation.RequestedValue, CancellationToken.None);
        await operation.RestoreAsync(snapshot, CancellationToken.None);

        network.Level.Should().Be(TcpAutotuningLevel.Restricted);
    }

    [Fact]
    public async Task AppxAndBootOperationsRefuseMutationWithoutExactRecovery()
    {
        var appx = new AppxPackageOperation(Descriptor("appx"), "Microsoft.Test");
        var boot = new BootSettingOperation(Descriptor("boot"), "nx");

        appx.IsSupported(default!).Should().BeFalse();
        boot.IsSupported(default!).Should().BeFalse();
        await Assert.ThrowsAsync<NotSupportedException>(() => appx.ApplyAsync(appx.RequestedValue, CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => boot.ApplyAsync(boot.RequestedValue, CancellationToken.None));
    }

    private static FixedProcessRunner RunnerFor(Func<ProcessStartInfo, TimeSpan, CancellationToken, Task<FixedProcessResult>> execute) =>
        new(null, new DelegateExecutor(execute));

    private static FixedProcessResult Success(string output = "") => new(0, output, string.Empty, false);
    private static TweakDescriptor Descriptor(string id) => new(id, id, TweakCategory.Windows, ImpactLevel.Low, RiskLevel.Safe, false, false);
    private static object Clone(object value) => value switch { byte[] bytes => bytes.ToArray(), string[] values => values.ToArray(), _ => value };
    private static bool Equivalent(object? actual, object expected) => actual switch { byte[] bytes when expected is byte[] wanted => bytes.SequenceEqual(wanted), string[] values when expected is string[] wanted => values.SequenceEqual(wanted), _ => Equals(actual, expected) };

    private sealed class DelegateExecutor(Func<ProcessStartInfo, TimeSpan, CancellationToken, Task<FixedProcessResult>> execute) : IFixedProcessExecutor
    {
        public List<ProcessStartInfo> Starts { get; } = [];
        public Task<FixedProcessResult> ExecuteAsync(ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Starts.Add(startInfo);
            return execute(startInfo, timeout, cancellationToken);
        }
    }

    private sealed class MemoryRegistry(RegistryRawValue value) : IRegistryStore
    {
        public RegistryRawValue Value { get; private set; } = value;
        public RegistryValue ReadCurrentUser(string key, string name) => RegistryValue.Missing;
        public void WriteCurrentUserDWord(string key, string name, int value) { }
        public void WriteCurrentUserText(string key, string name, string value) { }
        public void DeleteCurrentUserValue(string key, string name) { }
        public RegistryRawValue Read(RegistryHive hive, string key, string name) => Value;
        public void Write(RegistryHive hive, string key, string name, object value, RegistryValueKind kind) => Value = new(true, kind, Clone(value));
        public void Delete(RegistryHive hive, string key, string name) => Value = RegistryRawValue.Missing;
    }

    private sealed class ServiceMachine(ServiceStartup startup, bool running)
    {
        public ServiceStartup Startup { get; set; } = startup;
        public bool Running { get; set; } = running;
        public bool Missing { get; init; }
        public bool FailStop { get; init; }

        public Task<FixedProcessResult> ExecuteAsync(ProcessStartInfo start, TimeSpan timeout, CancellationToken token)
        {
            var arguments = start.ArgumentList;
            if (Missing) return Task.FromResult(new FixedProcessResult(1060, "", "The specified service does not exist.", false));
            return arguments[0] switch
            {
                "qc" => Task.FromResult(Success($"START_TYPE         : {(Startup == ServiceStartup.Boot ? 0 : Startup == ServiceStartup.System ? 1 : Startup is ServiceStartup.Auto or ServiceStartup.DelayedAuto ? 2 : Startup == ServiceStartup.Demand ? 3 : 4)} {(Startup == ServiceStartup.DelayedAuto ? "DELAYED_AUTO_START" : "")}")),
                "query" => Task.FromResult(Success($"STATE              : {(Running ? 4 : 1)}")),
                "config" => Configure(arguments),
                "start" => Start(),
                "stop" => Stop(),
                _ => Task.FromResult(Success())
            };
        }

        private Task<FixedProcessResult> Configure(IList<string> arguments)
        {
            Startup = arguments[3] switch { "boot" => ServiceStartup.Boot, "system" => ServiceStartup.System, "auto" => ServiceStartup.Auto, "delayed-auto" => ServiceStartup.DelayedAuto, "demand" => ServiceStartup.Demand, "disabled" => ServiceStartup.Disabled, _ => throw new InvalidOperationException() };
            return Task.FromResult(Success());
        }
        private Task<FixedProcessResult> Start() { Running = true; return Task.FromResult(Success()); }
        private Task<FixedProcessResult> Stop() => FailStop ? Task.FromResult(new FixedProcessResult(5, "", "stop denied", false)) : Task.FromResult(StopSuccess());
        private FixedProcessResult StopSuccess() { Running = false; return Success(); }
    }

    private sealed class TaskMachine
    {
        public bool Missing { get; init; }
        public bool Denied { get; init; }
        public bool Enabled { get; set; }
        public Task<FixedProcessResult> ExecuteAsync(ProcessStartInfo start, TimeSpan timeout, CancellationToken token)
        {
            var arguments = start.ArgumentList;
            if (arguments[0] == "/Query")
            {
                if (Missing) return Task.FromResult(new FixedProcessResult(1, "ERROR: The system cannot find the file specified.", "", false));
                if (Denied) return Task.FromResult(new FixedProcessResult(5, "", "Access is denied.", false));
                return Task.FromResult(Success($"Scheduled Task State: {(Enabled ? "Enabled" : "Disabled")}"));
            }
            Enabled = arguments.Contains("/Enable");
            return Task.FromResult(Success());
        }
    }

    private sealed class PowerMachine
    {
        public string Active { get; set; } = "11111111-1111-1111-1111-111111111111";
        public int Ac { get; set; } = 20;
        public int Dc { get; set; } = 10;
        public int SetCalls { get; private set; }
        public bool FailDcWrite { get; init; }
        public Task<FixedProcessResult> ExecuteAsync(ProcessStartInfo start, TimeSpan timeout, CancellationToken token)
        {
            var arguments = start.ArgumentList;
            if (arguments[0] == "/getactivescheme") return Task.FromResult(Success($"Power Scheme GUID: {Active}"));
            if (arguments[0] == "/query") return Task.FromResult(Success($"Current AC Power Setting Index: 0x{Ac:x8}\nCurrent DC Power Setting Index: 0x{Dc:x8}"));
            if (arguments[0] == "/setacvalueindex") { SetCalls++; Ac = int.Parse(arguments[4]); return Task.FromResult(Success()); }
            if (arguments[0] == "/setdcvalueindex")
            {
                SetCalls++;
                if (FailDcWrite) return Task.FromResult(new FixedProcessResult(1, "", "dc failed", false));
                Dc = int.Parse(arguments[4]);
            }
            return Task.FromResult(Success());
        }
    }

    private sealed class NetworkMachine
    {
        public TcpAutotuningLevel Level { get; set; }
        public Task<FixedProcessResult> ExecuteAsync(ProcessStartInfo start, TimeSpan timeout, CancellationToken token)
        {
            var arguments = start.ArgumentList;
            if (arguments.Contains("show")) return Task.FromResult(Success($"Receive Window Auto-Tuning Level : {Level switch { TcpAutotuningLevel.HighlyRestricted => "highlyrestricted", _ => Level.ToString().ToLowerInvariant() }}"));
            var wire = arguments.Single(value => value.StartsWith("autotuninglevel=", StringComparison.Ordinal))["autotuninglevel=".Length..];
            Level = wire switch { "normal" => TcpAutotuningLevel.Normal, "disabled" => TcpAutotuningLevel.Disabled, "restricted" => TcpAutotuningLevel.Restricted, "highlyrestricted" => TcpAutotuningLevel.HighlyRestricted, _ => throw new InvalidOperationException() };
            return Task.FromResult(Success());
        }
    }
}
