namespace Slh.Tms.Api.Models.Assistant;

public sealed class AssistantOptions
{
    public bool Enabled { get; set; }
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "gpt-4o";
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public int TimeoutSeconds { get; set; } = 25;

    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(ApiKey);
}
