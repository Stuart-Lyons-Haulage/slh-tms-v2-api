using Microsoft.Extensions.Configuration;
using System.Text.Json;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Jobs;

public sealed class EtaRecalculationJob(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    TmsDbContext db)
{
    public async Task<JobExecutionResult> RunAsync(CancellationToken ct)
    {
        var baseUrl = configuration["TmsApi:BaseUrl"]?.TrimEnd('/')
            ?? throw new InvalidOperationException("TmsApi:BaseUrl is required for the ETA recalculation job.");
        var wallboardKey = configuration["TvWallboard:AccessKey"]
            ?? throw new InvalidOperationException("TvWallboard:AccessKey is required for the ETA recalculation job.");

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v1/operations/delivery-etas");
        request.Headers.TryAddWithoutValidation(TvWallboardAccess.HeaderName, wallboardKey);
        using var response = await httpClientFactory.CreateClient("eta-job").SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!document.RootElement.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("ETA endpoint response did not contain a records array.");

        var samples = new List<EtaSnapshotCaptureItem>();
        foreach (var record in records.EnumerateArray())
        {
            if (!TryGuid(record, "loadId", out var loadId) || !TryGuid(record, "stopId", out var stopId)) continue;
            samples.Add(new EtaSnapshotCaptureItem(
                loadId,
                stopId,
                null,
                ReadDate(record, "etaUtc"),
                ReadString(record, "source") ?? "Unavailable",
                ReadString(record, "risk") ?? "Pending",
                ReadString(record, "tachoStatus") ?? "Unavailable",
                ReadInt(record, "breakMinutesIncluded"),
                ReadDate(record, "trackingUpdatedAtUtc")));
        }

        var added = await ManagementReportingStore.CaptureAsync(db, samples, ct);
        return new JobExecutionResult(true, $"Recalculated {samples.Count} ETA record(s) and persisted {added} precision snapshot(s).", added);
    }

    private static bool TryGuid(JsonElement element, string property, out Guid value)
    {
        value = Guid.Empty;
        return element.TryGetProperty(property, out var node) && node.ValueKind == JsonValueKind.String && Guid.TryParse(node.GetString(), out value);
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var node) && node.ValueKind == JsonValueKind.String ? node.GetString() : null;

    private static int ReadInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var node) && node.TryGetInt32(out var value) ? value : 0;

    private static DateTimeOffset? ReadDate(JsonElement element, string property) =>
        element.TryGetProperty(property, out var node) && node.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(node.GetString(), out var value) ? value : null;
}
