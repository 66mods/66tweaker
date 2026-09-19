using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;

namespace Tweaker.App.ViewModels;

/// <summary>One group as the assistant sees it: enough to answer "is it safe" and "do I restart".</summary>
public sealed record AskGroupFact(string Name, int Changes, int Permanent, bool RequiresRestart, bool IsRisky,
    string State, bool IsSelected, string Summary);

/// <summary>
/// Everything Ask 66 answers from, gathered at the moment a question is asked: hardware names, the state
/// of the seven groups, the chosen game profile and what it writes, and the last run's outcome. It never
/// leaves the PC — the assistant is a set of written answers chosen by keyword, not a model.
/// </summary>
public sealed record AskContext(
    string Cpu, string Gpu, string Windows,
    int? Score, int Measured, int Remaining,
    IReadOnlyList<AskGroupFact> Groups,
    IReadOnlyList<string> DetectedGames,
    string SelectedGame, string SelectedProfile, IReadOnlyList<string> ProfileLines, string DriverNote,
    string LastResult, IReadOnlyList<string> Issues)
{
    public static AskContext Empty { get; } = new("", "", "", null, 0, 0, [], [], "", "", [], "", "", []);
}

public sealed record AskMessage(bool IsUser, string Text)
{
    public string Role => IsUser ? "You" : "66";
}

/// <summary>
/// The Ask 66 panel: a chat-shaped FAQ. Every answer is written here and filled in from this PC's own
/// facts, so nothing leaves the machine and nothing needs a key.
/// </summary>
public sealed class AskViewModel : ObservableObject
{
    private readonly Func<AskContext> context;
    private bool isOpen;
    private string input = string.Empty;

    public AskViewModel(Func<AskContext> context)
    {
        this.context = context;
        SendCommand = new RelayCommand(() => Ask(Input));
        ToggleCommand = new RelayCommand(() => IsOpen = !IsOpen);
        CloseCommand = new RelayCommand(() => IsOpen = false);
        RefreshSuggestions();
    }

    public ObservableCollection<AskMessage> Messages { get; } = [];
    public ObservableCollection<string> Suggestions { get; } = [];
    public RelayCommand SendCommand { get; }
    public RelayCommand ToggleCommand { get; }
    public RelayCommand CloseCommand { get; }

    public bool IsOpen
    {
        get => isOpen;
        set
        {
            if (!Set(ref isOpen, value)) return;
            if (value) RefreshSuggestions();
        }
    }

    public string Input
    {
        get => input;
        set { if (Set(ref input, value)) RaisePropertyChanged(nameof(CanSend)); }
    }
    public bool CanSend => !string.IsNullOrWhiteSpace(Input);
    public bool HasMessages => Messages.Count > 0;

    public string ModeLabel => "Answers about this PC and the tweaks";
    public string FooterText => "Answers are written into the app and filled in from this PC · nothing is sent anywhere";

    /// <summary>Answers at once: every answer is written into the app and filled in from this PC.</summary>
    public void Ask(string question)
    {
        question = question.Trim();
        if (question.Length == 0) return;
        Input = string.Empty;
        Messages.Add(new(true, question));
        var facts = SafeContext();
        Messages.Add(new(false, LocalAnswers.Answer(question, facts)));
        RaisePropertyChanged(nameof(HasMessages));
        RefreshSuggestions(facts, question);
    }

    private void RefreshSuggestions(AskContext? facts = null, string? except = null)
    {
        facts ??= SafeContext();
        Suggestions.Clear();
        foreach (var suggestion in LocalAnswers.Suggestions(facts).Where(x => !string.Equals(x, except, StringComparison.OrdinalIgnoreCase)).Take(4))
            Suggestions.Add(suggestion);
    }

    private AskContext SafeContext()
    {
        try { return context(); }
        catch { return AskContext.Empty; }
    }
}

/// <summary>
/// The answers. Each is chosen by the words in the question and filled in from the facts, so "do I
/// need to restart" names the groups that actually need one on this PC. Not a chatbot: a FAQ that
/// reads like one.
/// </summary>
internal static partial class LocalAnswers
{
    public static IReadOnlyList<string> Suggestions(AskContext facts)
    {
        var list = new List<string>();
        var failed = facts.Groups.FirstOrDefault(x => x.State == "Failed");
        if (failed is not null) list.Add($"Why did {failed.Name} fail?");
        list.Add(facts.Score is { } score ? $"Why is my score {score}?" : "What is the score?");
        if (facts.SelectedGame.Length > 0) list.Add($"What does {facts.SelectedProfile} change in {facts.SelectedGame}?");
        list.Add("Do I need to restart?");
        list.Add("Is Debloat safe?");
        list.Add("Can I undo everything?");
        list.Add("Why does Windows call it a virus?");
        list.Add("Which profile should I pick?");
        return list;
    }

    public static string Answer(string question, AskContext facts)
    {
        // Specific before broad: "restore point" is a backup question before "restore" makes it an undo one,
        // and "still low fps at 1080p" is about the gain, not the resolution.
        if (FailPattern().IsMatch(question)) return FailAnswer(facts);
        if (VirusPattern().IsMatch(question)) return VirusAnswer();
        if (AdminPattern().IsMatch(question)) return AdminAnswer();
        if (BackupPattern().IsMatch(question)) return BackupAnswer();
        if (UndoPattern().IsMatch(question)) return UndoAnswer(facts);
        if (RestartPattern().IsMatch(question)) return RestartAnswer(facts);
        if (DebloatPattern().IsMatch(question)) return DebloatAnswer(facts);
        if (ScorePattern().IsMatch(question)) return ScoreAnswer(facts);
        if (NotDetectedPattern().IsMatch(question)) return DetectionAnswer(facts);
        if (WhichProfilePattern().IsMatch(question)) return WhichProfileAnswer(facts);
        if (VendorPattern().IsMatch(question)) return VendorAnswer(facts);
        if (NoGainPattern().IsMatch(question)) return NoGainAnswer(facts);
        if (ResolutionPattern().IsMatch(question)) return "Never. No profile and no group changes your desktop or in-game resolution, on any vendor. Frame rate comes from render scale, shadows, textures and driver settings, and every one of those is undone by Undo.";
        if (ProfilePattern().IsMatch(question) || facts.DetectedGames.Any(g => question.Contains(g, StringComparison.OrdinalIgnoreCase)))
            return ProfileAnswer(facts);
        var group = facts.Groups.FirstOrDefault(x => question.Contains(x.Name.Split(' ')[0], StringComparison.OrdinalIgnoreCase));
        if (group is not null) return GroupAnswer(group);
        if (OverviewPattern().IsMatch(question)) return OverviewAnswer(facts);
        return "I can answer about this PC and what the tweaker does: the score, any group, a game profile, restarts, undo, " +
            "backups, the administrator prompt, the antivirus warning. Try one of the questions below or name a group or a game.";
    }

    private static string OverviewAnswer(AskContext facts) =>
        $"This app changes Windows settings in seven groups ({facts.Groups.Sum(x => x.Changes)} changes) and writes game profiles for " +
        $"Fortnite, Valorant, GTA V, Minecraft and Roblox, including the graphics driver's own profile. Every value is recorded before " +
        "it is written and checked after, so Undo puts it back. Nothing is downloaded and nothing leaves this PC.";

    private static string ScoreAnswer(AskContext facts)
    {
        if (facts.Score is not { } score)
            return "The score has not been measured yet. It is the share of every setting this app can improve that " +
                "already holds the recommended value; it is read from Windows, never estimated.";
        var selected = facts.Groups.Where(x => x.IsSelected && x.State != "Applied").ToArray();
        var next = selected.Length == 0 ? "Pick groups on Optimize to raise it."
            : $"Running the {selected.Length} selected group(s) — {string.Join(", ", selected.Select(x => x.Name))} — is what moves it.";
        return $"{facts.Measured - facts.Remaining} of {facts.Measured} improvable settings already match the recommended value, " +
            $"so the score is {score}%; {facts.Remaining} are waiting. Debloat does not count, because uninstalls are a choice, not a fix. {next}";
    }

    private static string RestartAnswer(AskContext facts)
    {
        var restart = facts.Groups.Where(x => x.RequiresRestart).Select(x => x.Name).ToArray();
        var instant = facts.Groups.Where(x => !x.RequiresRestart).Select(x => x.Name).ToArray();
        var pending = facts.Groups.Where(x => x.RequiresRestart && x.State == "Applied").Select(x => x.Name).ToArray();
        var text = $"{Join(restart)} take effect after a restart; {Join(instant)} {(instant.Length == 1 ? "is" : "are")} instant. " +
            "Game profiles apply the next time the game starts. The app never restarts your PC by itself.";
        if (pending.Length > 0) text += $" Applied this session and still waiting for a restart: {Join(pending)}.";
        return text;
    }

    private static string DebloatAnswer(AskContext facts)
    {
        var debloat = facts.Groups.FirstOrDefault(x => x.Name.StartsWith("Debloat", StringComparison.OrdinalIgnoreCase));
        if (debloat is null) return "There is no Debloat group in this build.";
        return $"It is the one group Undo cannot fully reverse: {debloat.Permanent} of {debloat.Changes} changes uninstall apps. " +
            "Services can be re-enabled; a removed app only comes back from the Store. Every other group is fully reversible. " +
            "Skip it unless you know which apps you are losing — that is why it is not ticked by default.";
    }

    private static string UndoAnswer(AskContext facts)
    {
        var permanent = facts.Groups.Sum(x => x.Permanent);
        return "Undo rewinds every run of this session to the exact values recorded before it, newest first, and verifies " +
            "each restore. Restore, on the more menu, does the same for the last recorded session after the app was closed. " +
            (permanent > 0
                ? $"The exception is {permanent} uninstalls in Debloat & Services, which no rollback can bring back."
                : "Nothing in this build is permanent.") +
            " Game profiles have their own Undo, and Reset driver profile puts the driver back to how it was before the app first touched it, even after a restart of the app.";
    }

    private static string BackupAnswer() =>
        "Every value is recorded before it is written, into %LocalAppData%\\66mods Tweaker\\Transactions on this PC. Driver " +
        "profiles keep a baseline per vendor next to it. Before every group except Mouse & Keyboard the app also asks Windows " +
        "for a restore point. Nothing is uploaded anywhere.";

    private static string AdminAnswer() =>
        "Changing system settings needs administrator rights, so the app starts a small helper that asks Windows for them " +
        "once, lists exactly which operations it is about to run, and waits for your confirmation. The window you are looking " +
        "at never holds those rights itself. Decline the prompt and nothing is changed.";

    private static string VirusAnswer() =>
        "SmartScreen and Defender judge by reputation: an unsigned program that writes registry keys, changes services and asks " +
        "for administrator rights looks like what malware does, so a new build starts out flagged. The app is open source, the " +
        "release publishes its SHA-256, and every change it makes is listed in the app and recorded before it is written. " +
        "Check the hash from the release page before running; the warning goes away once the build has gathered reputation or is signed.";

    private static string DetectionAnswer(AskContext facts) =>
        facts.DetectedGames.Count == 0
            ? "No game was found in its usual install folder. Launch the game once so it creates its settings, then choose Rescan this PC from the ··· menu. " +
              "Games installed to an unusual folder are not found yet; the profile needs the game's own settings file."
            : $"Found: {string.Join(", ", facts.DetectedGames)}. A game that is installed but not listed has its settings in an " +
              "unusual place; launch it once and choose Rescan this PC from the ··· menu. Roblox is found by its launcher, so it appears after the first start.";

    private static string WhichProfileAnswer(AskContext facts) =>
        "Balanced keeps the picture and only tidies the driver. Competitive trades a little sharpness for latency; it is the " +
        "right pick for shooters on a mid-range PC. Mega FPS blurs textures and drops filtering for a real frame gain. Ultra Potato " +
        "is for weak laptops and integrated graphics: the most frames the game allows, at the cost of how it looks. " +
        (facts.SelectedGame.Length > 0 ? $"You have {facts.SelectedProfile} selected for {facts.SelectedGame}. " : "") +
        "Every step can be undone.";

    private static string VendorAnswer(AskContext facts) =>
        (facts.DriverNote.Length > 0 ? facts.DriverNote + " " : "") +
        "NVIDIA and Intel profiles are written per game, for that game's executable only. AMD's interface has no per-game " +
        "scope, so on a Radeon the profile applies to every game until Undo or Reset. A PC whose driver offers none of these " +
        "still gets the game's own settings, which are the larger half of the gain.";

    private static string NoGainAnswer(AskContext facts)
    {
        var restart = facts.Groups.Where(x => x.RequiresRestart && x.State == "Applied").Select(x => x.Name).ToArray();
        return (restart.Length > 0 ? $"{Join(restart)} only take effect after a restart, so restart first. " : "") +
            "A game profile applies when the game starts, so restart the game too. Then compare with the game's own frame " +
            "counter over a minute of play, not a loading screen. If the picture changed but the frames did not, the PC is " +
            "limited by something these settings do not touch — usually the GPU at that resolution or thermal throttling.";
    }

    private static string FailAnswer(AskContext facts)
    {
        var failed = facts.Groups.Where(x => x.State == "Failed").ToArray();
        if (failed.Length == 0 && facts.Issues.Count == 0)
            return "Nothing has failed in this session. " + (facts.LastResult.Length > 0 ? $"Last result: {facts.LastResult}" : "");
        var text = new StringBuilder();
        if (failed.Length > 0)
            text.Append($"{Join(failed.Select(x => x.Name).ToArray())} did not apply. Nothing is half-applied: the group rolled " +
                "back and verified the restore. ");
        if (facts.Issues.Count > 0)
            text.Append($"What was refused: {string.Join(" · ", facts.Issues.Take(3))}. ");
        text.Append("Close anything that pins those settings (a GPU control panel, a game), press Retry, and if it fails " +
            "again attach worker.log from C:\\ProgramData\\66mods Tweaker to a bug report.");
        return text.ToString();
    }

    private static string ProfileAnswer(AskContext facts)
    {
        if (facts.SelectedGame.Length == 0) return "Pick a game on the Games page and I will tell you what its profile writes.";
        if (facts.ProfileLines.Count == 0)
            return $"{facts.SelectedProfile} writes nothing for {facts.SelectedGame} on this PC — the game was not found. " +
                "Install it, then choose Rescan this PC from the ··· menu, and the profile becomes available.";
        var lines = facts.ProfileLines.Take(8).ToArray();
        var more = facts.ProfileLines.Count > lines.Length ? $" and {facts.ProfileLines.Count - lines.Length} more" : "";
        return $"{facts.SelectedProfile} for {facts.SelectedGame} writes, in one transaction: {string.Join("; ", lines)}{more}. " +
            "Your monitor resolution stays as it is, and Undo restores every value. " + facts.DriverNote;
    }

    private static string GroupAnswer(AskGroupFact group) =>
        $"{group.Name}: {group.Summary} {group.Changes} changes, {group.Permanent} of them permanent, " +
        $"{(group.RequiresRestart ? "takes effect after a restart" : "instant")}. State right now: {group.State}.";

    private static string Join(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "none",
        1 => names[0],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1]
    };

    [GeneratedRegex(@"\b(fail|failed|error|refus|retry|wrong|broke)", RegexOptions.IgnoreCase)]
    private static partial Regex FailPattern();
    [GeneratedRegex(@"\b(virus|malware|malicious|defender|smartscreen|antivirus|trojan|safe to run|trust (it|this|you|the app))", RegexOptions.IgnoreCase)]
    private static partial Regex VirusPattern();
    [GeneratedRegex(@"\b(admin|administrator|uac|prompt|elevat|permission)", RegexOptions.IgnoreCase)]
    private static partial Regex AdminPattern();
    [GeneratedRegex(@"\b(restart|reboot)", RegexOptions.IgnoreCase)]
    private static partial Regex RestartPattern();
    [GeneratedRegex(@"\b(debloat|uninstall|bloat)", RegexOptions.IgnoreCase)]
    private static partial Regex DebloatPattern();
    [GeneratedRegex(@"\b(undo|revert|rollback|roll back|restore|reset|go back)", RegexOptions.IgnoreCase)]
    private static partial Regex UndoPattern();
    [GeneratedRegex(@"\b(backup|back up|journal|where .*(saved|stored)|snapshot|restore point)", RegexOptions.IgnoreCase)]
    private static partial Regex BackupPattern();
    [GeneratedRegex(@"\b(score|optimi[sz]ed|waiting|percent|%)", RegexOptions.IgnoreCase)]
    private static partial Regex ScorePattern();
    [GeneratedRegex(@"\b(not (detected|found|installed)|can.t find|doesn.t see|missing game|detect)", RegexOptions.IgnoreCase)]
    private static partial Regex NotDetectedPattern();
    [GeneratedRegex(@"\b(which|what) (profile|one)|should i (pick|choose|use)|best profile|recommend", RegexOptions.IgnoreCase)]
    private static partial Regex WhichProfilePattern();
    [GeneratedRegex(@"\b(amd|radeon|intel|arc|nvidia|geforce|driver|vendor|gpu|graphics card)", RegexOptions.IgnoreCase)]
    private static partial Regex VendorPattern();
    [GeneratedRegex(@"\b(resolution|1080|1440|monitor)", RegexOptions.IgnoreCase)]
    private static partial Regex ResolutionPattern();
    [GeneratedRegex(@"\b(no (difference|change|gain|effect)|didn.t (help|work)|not working|same fps|still (low|lag)|no fps)", RegexOptions.IgnoreCase)]
    private static partial Regex NoGainPattern();
    [GeneratedRegex(@"\b(potato|profile|mega|competitive|balanced|game|fps)", RegexOptions.IgnoreCase)]
    private static partial Regex ProfilePattern();
    [GeneratedRegex(@"\b(what (is|does) this|help|how (does|do) (it|this|you)|what can you|about)", RegexOptions.IgnoreCase)]
    private static partial Regex OverviewPattern();
}
