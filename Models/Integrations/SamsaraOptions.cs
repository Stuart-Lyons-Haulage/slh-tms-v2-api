namespace Slh.Tms.Api.Models.Integrations;

public sealed class SamsaraOptions
{
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "https://api.samsara.com";
    public string ApiToken { get; set; } = string.Empty;

    // External IDs are deliberately split by entity so Samsara can be reconciled
    // safely without creating duplicate routes, stops or reusable addresses.
    public string ExternalIdKey { get; set; } = "slhTmsRun";
    public string StopExternalIdKey { get; set; } = "slhTmsStop";
    public string SiteExternalIdKey { get; set; } = "slhTmsSite";

    public int StopRadiusMeters { get; set; } = 250;

    // SLH TMS is the planning authority. Samsara executes and reports progress;
    // it must not rewrite the HGV-aware schedule created by the TMS.
    public bool RecomputeScheduledTimes { get; set; } = false;
    public string RouteStartingCondition { get; set; } = "departFirstStop";
    public string RouteCompletionCondition { get; set; } = "arriveLastStop";
    public string SequencingMethod { get; set; } = "manual";

    // Known Site Master locations should be reusable Samsara Addresses. Single-use
    // locations remain as a safe fallback for an ad-hoc stop that cannot be mapped.
    public bool EnableAddressSync { get; set; } = true;

    public bool IsConfigured =>
        Enabled &&
        !string.IsNullOrWhiteSpace(BaseUrl) &&
        !string.IsNullOrWhiteSpace(ApiToken) &&
        !string.IsNullOrWhiteSpace(ExternalIdKey) &&
        !string.IsNullOrWhiteSpace(StopExternalIdKey) &&
        !string.IsNullOrWhiteSpace(SiteExternalIdKey);

    public string[] MissingSettings => new[]
    {
        Enabled ? string.Empty : "Samsara enabled flag",
        string.IsNullOrWhiteSpace(BaseUrl) ? "Samsara base URL" : string.Empty,
        string.IsNullOrWhiteSpace(ApiToken) ? "Samsara API token" : string.Empty,
        string.IsNullOrWhiteSpace(ExternalIdKey) ? "Samsara route external ID key" : string.Empty,
        string.IsNullOrWhiteSpace(StopExternalIdKey) ? "Samsara stop external ID key" : string.Empty,
        string.IsNullOrWhiteSpace(SiteExternalIdKey) ? "Samsara site external ID key" : string.Empty
    }.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
}
