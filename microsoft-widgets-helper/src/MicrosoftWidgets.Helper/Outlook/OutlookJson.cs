using System.Globalization;
using System.Text.Json;

namespace PlannerEdge.Helper.Outlook;

internal static class OutlookJson
{
    internal static string Text(this JsonElement json, string name, string fallback = "") => json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
    internal static bool Flag(this JsonElement json, string name) => json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    internal static JsonElement Child(this JsonElement json, string name) => json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var value) ? value : default;
    internal static IEnumerable<JsonElement> Items(this JsonElement json) => json.ValueKind == JsonValueKind.Array ? json.EnumerateArray() : [];
    internal static string Person(JsonElement json)
    {
        var address = json.Text("address");
        var name = json.Text("name");
        return name.Length == 0 ? address : address.Length == 0 || address == name ? name : $"{name} <{address}>";
    }

    internal static string EventTime(JsonElement json, bool allDay)
    {
        var value = json.Text("dateTime");
        if (allDay && DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) return day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (HasOffset(value) && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant)) return instant.ToUniversalTime().ToString("O");
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            try
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById(json.Text("timeZone", "UTC"));
                return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone)).ToString("O");
            }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
            catch (ArgumentException) { }
        }
        throw new OutlookException("invalid_response", "Outlook returned an unsupported event time.");
    }

    internal static bool HasOffset(string? value) => value is not null && value.Contains('T') && (value.EndsWith('Z') || value.LastIndexOf('+') > value.IndexOf('T') || value.LastIndexOf('-') > value.IndexOf('T'));
}
