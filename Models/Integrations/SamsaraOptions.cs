namespace Slh.Tms.Api.Models.Integrations;

public sealed class SamsaraOptions
{
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "https://api.samsara.com";
    public string ApiToken { get; set; } = string.Empty;
    public string ExternalIdKey { get; set; } = "slhTmsRun";
    public int StopRadiusMeters { get; set; } = 250;

    public bool IsConfigured =>
        Enabled &&
        !string.IsNullOrWhiteSpace(BaseUrl) &&
        !string.IsNullOrWhiteSpace(ApiToken) &&
        !string.IsNullOrWhiteSpace(ExternalIdKey);

    public string[] MissingSettings => new[]
    {
        Enabled ? string.Empty : "Samsara enabled flag",
        string.IsNullOrWhiteSpace(BaseUrl) ? "Samsara base URL" : string.Empty,
        string.IsNullOrWhiteSpace(ApiToken) ? "Samsara API token" : string.Empty,
        string.IsNullOrWhiteSpace(ExternalIdKey) ? "Samsara external ID key" : string.Empty
    }.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
}
