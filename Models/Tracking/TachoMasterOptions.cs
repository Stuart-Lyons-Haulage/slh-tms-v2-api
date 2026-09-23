namespace Slh.Tms.Api.Models.Tracking;

public sealed class TachoMasterOptions
{
    public string BaseUrl { get; set; } = "https://api-v1-alpha.roadtech.co.uk";
    public string ApiKey { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public int MaxPages { get; set; } = 20;
    public bool UsesSharedRoadTechCredentials { get; set; }

    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(BaseUrl) &&
        !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(Username) &&
        !string.IsNullOrWhiteSpace(Password);
}
