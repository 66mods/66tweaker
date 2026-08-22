namespace Tweaker.App.ViewModels;

/// <summary>Severity of one console line, taken from the four-character status column the worker writes.</summary>
public enum ApplyLogKind { Info, Ok, Skip, Fail }

/// <summary>
/// One console line. The status column stays in <see cref="Text"/> so a copied log reads the same outside
/// the app, and <see cref="Kind"/> only drives colour.
/// </summary>
public sealed record ApplyLogLine(string Text, ApplyLogKind Kind)
{
    private const string Ok = "  ok";
    private const string Skip = "skip";
    private const string Fail = "FAIL";

    public static ApplyLogLine Parse(string line) => new(line, KindOf(line));

    private static ApplyLogKind KindOf(string line)
    {
        if (line.StartsWith(Fail, StringComparison.Ordinal)) return ApplyLogKind.Fail;
        if (line.StartsWith(Ok, StringComparison.Ordinal)) return ApplyLogKind.Ok;
        if (line.StartsWith(Skip, StringComparison.Ordinal)) return ApplyLogKind.Skip;
        return ApplyLogKind.Info;
    }

    /// <summary>String form so the view can pick a brush without knowing the enum.</summary>
    public string KindName => Kind.ToString();
}
