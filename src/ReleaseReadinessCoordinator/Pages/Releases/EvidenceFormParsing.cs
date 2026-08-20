using System.Globalization;
using ReleaseReadinessCoordinator.Domain;

namespace ReleaseReadinessCoordinator.Pages.Releases;

internal static class EvidenceFormParsing
{
    public static UtcInstant Instant(DateTimeOffset value) =>
        new(value.ToUniversalTime());

    public static UtcInstant? Instant(DateTimeOffset? value) =>
        value.HasValue ? Instant(value.Value) : null;

    public static UtcInterval Interval(DateTimeOffset start, DateTimeOffset end) =>
        new(Instant(start), Instant(end));

    public static UtcInterval? OptionalInterval(
        DateTimeOffset? start,
        DateTimeOffset? end,
        string fieldName)
    {
        if (!start.HasValue && !end.HasValue)
        {
            return null;
        }

        if (!start.HasValue || !end.HasValue)
        {
            throw new FormatException($"The {fieldName} requires both a start and an end.");
        }

        return Interval(start.Value, end.Value);
    }

    public static string[] ParseList(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split(
                [',', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static IReadOnlyDictionary<string, (string Scope, UtcInstant ExpiresAt)>
        ParseExceptions(string? text)
    {
        var exceptions = new Dictionary<string, (string Scope, UtcInstant ExpiresAt)>(
            StringComparer.Ordinal);
        foreach (var line in Lines(text))
        {
            var fields = line.Split('|', StringSplitOptions.TrimEntries);
            if (fields.Length != 3
                || fields.Take(2).Any(string.IsNullOrWhiteSpace)
                || !TryParseExpiry(fields[2], out var expiry))
            {
                throw new FormatException(
                    "Each security exception line must use finding-id|scope|expiry-with-offset.");
            }

            if (!exceptions.TryAdd(fields[0], (fields[1], Instant(expiry))))
            {
                throw new FormatException(
                    $"Security exceptions contain duplicate finding '{fields[0]}'.");
            }
        }

        return exceptions;
    }

    private static bool TryParseExpiry(string text, out DateTimeOffset expiry) =>
        DateTimeOffset.TryParseExact(
            text,
            "yyyy-MM-dd HH:mm:ss 'UTC'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out expiry)
        || DateTimeOffset.TryParse(text, out expiry);

    private static IEnumerable<string> Lines(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
