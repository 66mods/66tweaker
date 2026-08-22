using System.Text.RegularExpressions;

namespace Tweaker.Domain.Privilege;

public sealed record PrivilegedOperationRequest(string OperationId, string RequestedValueId)
{
    private static readonly Regex IdPattern = new(
        "^[a-z0-9](?:[a-z0-9.-]{0,126}[a-z0-9])?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public void Validate()
    {
        if (!IsCanonicalId(OperationId))
            throw new InvalidDataException("The privileged operation ID is invalid.");
        if (!IsCanonicalId(RequestedValueId))
            throw new InvalidDataException("The privileged catalog value ID is invalid.");
    }

    public static bool IsCanonicalId(string? value) =>
        value is { Length: > 0 and <= 128 } && IdPattern.IsMatch(value);
}

public sealed record PrivilegedPlan(
    Guid TransactionId,
    int SchemaVersion,
    IReadOnlyList<PrivilegedOperationRequest> Operations,
    string Integrity)
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumOperations = 128;

    public void ValidateShape()
    {
        if (TransactionId == Guid.Empty)
            throw new InvalidDataException("The privileged transaction ID is invalid.");
        if (SchemaVersion != CurrentSchemaVersion)
            throw new InvalidDataException("The privileged plan schema is unsupported.");
        if (Operations is null || Operations.Count is < 1 or > MaximumOperations)
            throw new InvalidDataException("The privileged plan operation count is invalid.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operation in Operations)
        {
            operation.Validate();
            if (!seen.Add(operation.OperationId))
                throw new InvalidDataException("Duplicate privileged operations are not allowed.");
        }

        if (Integrity.Length != 64 || !Integrity.All(Uri.IsHexDigit))
            throw new InvalidDataException("The privileged plan integrity value is invalid.");
    }
}
