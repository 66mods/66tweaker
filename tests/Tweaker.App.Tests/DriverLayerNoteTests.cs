using FluentAssertions;
using Tweaker.App.ViewModels;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Games;
using Tweaker.Domain.Models;
using Tweaker.Domain.Services;

namespace Tweaker.App.Tests;

/// <summary>
/// A machine with no writable driver interface has to be told so.
///
/// Before this, an AMD or Intel PC got the client half of a Roblox profile and no indication that the
/// driver half had not happened — the page looked identical to an NVIDIA one. The previous attempt at
/// saying so was computed into a collection nothing was bound to, so it never reached a screen at all;
/// that is why this asserts on the text rather than on the property existing.
/// </summary>
public sealed class DriverLayerNoteTests
{
    [Theory]
    [InlineData("AMD", "Radeon RX 6600")]
    [InlineData("Intel", "Intel Arc A750")]
    public void AGpuWithNoWritableInterfaceSaysSoAndNamesItself(string vendor, string model)
    {
        var vm = Build(vendor, model);
        vm.SelectedGame = "Roblox";

        vm.HasDriverLayerNote.Should().BeTrue();
        vm.DriverLayerNote.Should().Contain(vendor, "the player should see their own hardware named");
        vm.DriverLayerNote.Should().Contain("client",
            "the note has to say what still happens, not only what does not");
    }

    [Fact]
    public void TheNoteIsNotShownForGamesThatHaveNoDriverLayerAtAll()
    {
        // Only Roblox has a driver half. On the other games the note would be answering a question the
        // page never raised.
        var vm = Build("AMD", "Radeon RX 6600");
        vm.SelectedGame = "Fortnite";

        vm.HasDriverLayerNote.Should().BeFalse();
        vm.DriverLayerNote.Should().BeEmpty();
    }

    [Fact]
    public void ChangingTheSelectedGameReRaisesTheNote()
    {
        // The note is derived, so without a change notification it would be correct and invisible.
        var vm = Build("AMD", "Radeon RX 6600");
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        vm.SelectedGame = "Roblox";

        raised.Should().Contain(nameof(GameProfilesViewModel.HasDriverLayerNote));
    }

    private static GameProfilesViewModel Build(string vendor, string model)
    {
        var snapshot = new SystemSnapshot(new("Windows 11", "10.0.26100", 26100), new("CPU", "AMD"),
            [new(model, vendor, "1.0")], new(8_000_000_000), new(false, true, "Balanced"),
            new Dictionary<string, DetectedGame>(), []);
        return new GameProfilesViewModel(snapshot, new TransactionCoordinator(new MemoryStore()));
    }

    private sealed class MemoryStore : ITransactionStore
    {
        private readonly Dictionary<Guid, TransactionRecord> records = [];
        public Task BeginAsync(TransactionRecord transaction, CancellationToken cancellationToken)
        {
            records[transaction.Id] = transaction;
            return Task.CompletedTask;
        }
        public Task SaveAsync(TransactionRecord transaction, CancellationToken cancellationToken)
        {
            records[transaction.Id] = transaction;
            return Task.CompletedTask;
        }
        public Task<TransactionRecord?> LoadAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(records.GetValueOrDefault(id));
        public Task<TransactionRecord?> LoadLatestIncompleteAsync(CancellationToken cancellationToken) =>
            Task.FromResult<TransactionRecord?>(null);
    }
}
