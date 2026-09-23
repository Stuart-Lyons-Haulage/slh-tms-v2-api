namespace Slh.Tms.Api.Models.Integrations;

public sealed class TextBeeOptions
{
    public string BaseUrl { get; set; } = "https://api.textbee.dev";
    public string ApiKey { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string DutyPhoneLabel { get; set; } = "Duty phone";
    public string WebhookSigningSecret { get; set; } = string.Empty;
    public bool Enabled { get; set; }

    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(DeviceId);
    public string[] MissingSettings => new[]
    {
        Enabled ? string.Empty : "TextBee enabled flag",
        string.IsNullOrWhiteSpace(BaseUrl) ? "TextBee base URL" : string.Empty,
        string.IsNullOrWhiteSpace(ApiKey) ? "TextBee access token" : string.Empty,
        string.IsNullOrWhiteSpace(DeviceId) ? "TextBee device ID" : string.Empty
    }.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
}
