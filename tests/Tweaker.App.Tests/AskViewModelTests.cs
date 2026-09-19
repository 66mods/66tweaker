using FluentAssertions;
using Tweaker.App.ViewModels;

namespace Tweaker.App.Tests;

/// <summary>
/// Ask 66 is a FAQ shaped like a chat: every answer is written into the app and filled in from this PC's
/// facts. These pin that the right answer is chosen for the words people actually use, and that it says
/// the facts of this machine rather than a generic sentence.
/// </summary>
public sealed class AskViewModelTests
{
    private static AskContext Facts(string failedGroup = "") => new(
        "AMD Ryzen 7 5800X", "NVIDIA GeForce RTX 3060 Ti", "Windows 11", 32, 38, 26,
        [
            new("Power & CPU", 207, 0, true, false, "Ready", true, "Unparks cores."),
            new("Mouse & Keyboard", 48, 0, false, false, "Applied", false, "Removes pointer acceleration."),
            new("GPU & DirectX", 372, 19, true, false, failedGroup == "GPU & DirectX" ? "Failed" : "Ready", true, "Driver-level latency."),
            new("Debloat & Services", 108, 68, true, true, "Risky", false, "Removes bundled apps.")
        ],
        ["Fortnite"], "Fortnite", "Ultra Potato",
        ["Fortnite · Render scale: 50%", "NVIDIA · Low latency mode: On"],
        "NVIDIA: per game, for FortniteClient-Win64-Shipping.exe. Undo restores every value.",
        "Applied 207 local mutation(s).", failedGroup.Length == 0 ? [] : ["FAIL 12/372 [nvidia] reg add ... -> Access is denied."]);

    private static AskViewModel Build(string failedGroup = "") => new(() => Facts(failedGroup));

    [Fact]
    public void ARestartQuestionNamesTheGroupsOfThisPc()
    {
        var vm = Build();

        vm.Ask("Do I need to restart?");

        vm.Messages.Should().HaveCount(2);
        vm.Messages[0].IsUser.Should().BeTrue();
        vm.Messages[1].Text.Should().Contain("Power & CPU").And.Contain("restart").And.Contain("Mouse & Keyboard");
        vm.Input.Should().BeEmpty();
        vm.HasMessages.Should().BeTrue();
    }

    [Theory]
    [InlineData("Does it make a restore point?", "restore point")]
    [InlineData("Can I undo without a restart?", "Undo rewinds")]
    [InlineData("Can I trust Undo?", "Undo rewinds")]
    [InlineData("still low fps at 1080p", "frame counter")]
    [InlineData("Is it a virus? Can I trust it?", "SmartScreen")]
    [InlineData("Does it change my resolution?", "Never.")]
    public void TheSpecificQuestionWinsOverTheBroadWordInsideIt(string question, string expected)
    {
        // Every trigger is a word list; "restore point" contains "restore", "1080p" is a fps complaint, and
        // "trust Undo" is about Undo. Order and anchoring decide which answer comes back.
        var vm = Build();

        vm.Ask(question);

        vm.Messages[1].Text.Should().Contain(expected);
    }

    [Fact]
    public void EverySuggestionLandsOnAnAnswerOfItsOwn()
    {
        var vm = Build("GPU & DirectX");
        var answers = new List<string>();
        foreach (var suggestion in LocalAnswers.Suggestions(Facts("GPU & DirectX")))
        {
            vm.Ask(suggestion);
            answers.Add(vm.Messages[^1].Text);
        }

        answers.Should().OnlyHaveUniqueItems("two suggestions with the same answer would be one suggestion");
        answers.Should().NotContain(x => x.StartsWith("I can answer about this PC"), "a suggestion is never answered with the fallback");
    }

    [Theory]
    [InlineData("Why is my score 32?", "12 of 38")]
    [InlineData("Is Debloat safe?", "68 of 108")]
    [InlineData("Can I undo everything?", "Undo rewinds")]
    [InlineData("Why does Windows call it a virus?", "reputation")]
    [InlineData("Why does it ask for administrator?", "administrator")]
    [InlineData("Where are my backups?", "Transactions")]
    [InlineData("Which profile should I pick?", "Competitive")]
    [InlineData("Does it change my resolution?", "Never")]
    [InlineData("It didn't help, same fps", "frame counter")]
    [InlineData("Does AMD work?", "Radeon")]
    [InlineData("What does Ultra Potato change in Fortnite?", "Render scale: 50%")]
    [InlineData("What is this app?", "seven groups")]
    [InlineData("Tell me about Power", "Power & CPU:")]
    public void TheWordsPeopleUseChooseTheRightAnswer(string question, string expected)
    {
        var vm = Build();

        vm.Ask(question);

        vm.Messages[1].Text.Should().Contain(expected);
    }

    [Fact]
    public void AQuestionItCannotAnswerSaysWhatItCan()
    {
        var vm = Build();

        vm.Ask("What is the weather like?");

        vm.Messages[1].Text.Should().Contain("this PC").And.Contain("questions below");
    }

    [Fact]
    public void SuggestionsLeadWithTheFailureWhenThereIsOne()
    {
        var vm = Build("GPU & DirectX");

        vm.Suggestions.First().Should().Be("Why did GPU & DirectX fail?");
        vm.Suggestions.Should().HaveCount(4);
    }

    [Fact]
    public void TheFailureAnswerNamesTheGroupAndQuotesTheRefusal()
    {
        var vm = Build("GPU & DirectX");

        vm.Ask("Why did GPU & DirectX fail?");

        vm.Messages[1].Text.Should().Contain("GPU & DirectX").And.Contain("Access is denied").And.Contain("rolled");
    }

    [Fact]
    public void NoGameFoundIsExplainedWithTheNextStep()
    {
        var vm = new AskViewModel(() => Facts() with { DetectedGames = [] });

        vm.Ask("My game is not detected");

        vm.Messages[1].Text.Should().Contain("Rescan this PC");
    }

    [Fact]
    public void TheHeaderAndFooterSayNothingLeavesThePc()
    {
        var vm = Build();

        vm.FooterText.Should().Contain("nothing is sent");
        vm.ModeLabel.Should().NotContainEquivalentOf("DeepSeek");
        vm.FooterText.Should().NotContainEquivalentOf("DeepSeek");
    }

    [Fact]
    public void ABrokenContextStillAnswers()
    {
        var vm = new AskViewModel(() => throw new InvalidOperationException("no snapshot yet"));

        vm.Ask("Do I need to restart?");

        vm.Messages[1].Text.Should().NotBeEmpty();
    }
}
