using System.Globalization;
using ReleaseReadinessCoordinator.Domain;

namespace ReleaseReadinessCoordinator.Pages.Releases;

internal static class LocalDateTimeDisplay
{
    public const string TextFormat = "yyyy-MM-dd HH:mm:ss";

    public static string Format(UtcInstant instant) =>
        ToLocal(instant.Value).ToString(TextFormat, CultureInfo.InvariantCulture);

    public static DateTimeOffset ToLocal(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.Local);

    public static DateTimeOffset? ToLocal(DateTimeOffset? instant) =>
        instant.HasValue ? ToLocal(instant.Value) : null;
}
