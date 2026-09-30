namespace Slh.Tms.Api.Models.Integrations;

public sealed class SamsaraOptions
{
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "https://api.eu.samsara.com";
    public string ApiToken { get; set; } = string.Empty;

    // External IDs are deliberately split by entity so Samsara can be reconciled
    // safely without creating duplicate routes, stops or reusable addresses.
    public string ExternalIdKey { get; set; } = "slhTmsRun";
    public string StopExternalIdKey { get; set; } = "slhTmsStop";
    public string SiteExternalIdKey { get; set; } = "slhTmsSite";
    public string AssetExternalIdKey { get; set; } = "slhTmsAsset";

    public int StopRadiusMeters { get; set; } = 250;
    public int WalkaroundMinutes { get; set; } = 10;
    public int DefaultStopDwellMinutes { get; set; } = 30;

    // SLH TMS owns the legal start, route order and stop constraints. Samsara
    // calculates the intermediate travel schedule from the first-stop start.
    public bool RecomputeScheduledTimes { get; set; } = true;
    public string RouteStartingCondition { get; set; } = "departFirstStop";
    public string RouteCompletionCondition { get; set; } = "departLastStop";
    public string SequencingMethod { get; set; } = "manual";

    // Known Site Master locations should be reusable Samsara Addresses. Single-use
    // locations remain as a safe fallback for an ad-hoc stop that cannot be mapped.
    public bool EnableAddressSync { get; set; } = true;

    // The Assets API is the outbound master-data path for vehicles and trailers.
    // Keep this explicit so an organisation can run address/route sync without
    // creating asset records until the Samsara permission has been verified.
    public bool EnableAssetSync { get; set; } = true;

    // Route execution is consumed from Samsara's append-only audit feed. The TMS
    // stores only the latest stop snapshot plus the feed cursor, not raw events.
    public bool EnableRouteProgressSync { get; set; } = true;
    public int RouteProgressPollSeconds { get; set; } = 30;

    public bool IsConfigured =>
        Enabled &&
        !string.IsNullOrWhiteSpace(BaseUrl) &&
        !string.IsNullOrWhiteSpace(ApiToken) &&
        !string.IsNullOrWhiteSpace(ExternalIdKey) &&
        !string.IsNullOrWhiteSpace(StopExternalIdKey) &&
        !string.IsNullOrWhiteSpace(SiteExternalIdKey) &&
        !string.IsNullOrWhiteSpace(AssetExternalIdKey);

    public string[] MissingSettings => new[]
    {
        Enabled ? string.Empty : "Samsara enabled flag",
        string.IsNullOrWhiteSpace(BaseUrl) ? "Samsara base URL" : string.Empty,
        string.IsNullOrWhiteSpace(ApiToken) ? "Samsara API token" : string.Empty,
        string.IsNullOrWhiteSpace(ExternalIdKey) ? "Samsara route external ID key" : string.Empty,
        string.IsNullOrWhiteSpace(StopExternalIdKey) ? "Samsara stop external ID key" : string.Empty,
        string.IsNullOrWhiteSpace(SiteExternalIdKey) ? "Samsara site external ID key" : string.Empty,
        string.IsNullOrWhiteSpace(AssetExternalIdKey) ? "Samsara asset external ID key" : string.Empty
    }.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
}
