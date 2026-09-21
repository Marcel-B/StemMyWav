using System.Text.Json.Serialization;

namespace StemMyWav.Gateway.Jobs;

/// <summary>Lebenszyklus eines Auftrags. Die Namen sind Teil der HTTP-Antwort und des
/// persistierten Formats, deshalb sind sie explizit festgelegt statt aus dem Enum abgeleitet.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<JobStatus>))]
public enum JobStatus
{
    [JsonStringEnumMemberName("queued")] Queued,
    [JsonStringEnumMemberName("processing")] Processing,
    [JsonStringEnumMemberName("completed")] Completed,
    [JsonStringEnumMemberName("failed")] Failed
}
