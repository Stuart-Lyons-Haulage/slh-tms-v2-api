namespace Slh.Tms.Api.Models.Tracking;

/// <summary>
/// Runtime configuration for the RoadTech Falcon tracking integration.
/// Credentials are supplied only from Azure Key Vault or environment variables.
/// </summary>
public sealed class DotTrackingOptions
{
    private int _dataMask = 0x01 | 0x04;
    private string _baseUrl = string.Empty;

    public string BaseUrl
    {
        get => _baseUrl;
        set
        {
            var candidate = (value ?? string.Empty).Trim();
            if (candidate.Length == 0)
            {
                _baseUrl = string.Empty;
                BaseUrlConfigurationError = null;
                return;
            }

            if (!candidate.Contains("://", StringComparison.Ordinal))
                candidate = $"https://{candidate.TrimStart('/')}";

            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                _baseUrl = string.Empty;
                BaseUrlConfigurationError = "RoadTech base URL is invalid; expected an absolute HTTP(S) URL such as https://api-v1.roadtech.co.uk.";
                return;
            }

            _baseUrl = uri.ToString().TrimEnd('/');
            BaseUrlConfigurationError = null;
        }
    }

    public string? BaseUrlConfigurationError { get; private set; }
    public string ApiKey { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string CompanyCode { get; set; } = string.Empty;
    public int PollIntervalMinutes { get; set; } = 1;
    public int RecoveryIntervalMinutes { get; set; } = 60;
    public bool Enabled { get; set; }
    public int StaleAfterMinutes { get; set; } = 10;

    /// <summary>
    /// RoadTech telemetry data mask. GPS is bit 0x01 and CAN is bit 0x04.
    /// Preserve both bits even if an older Azure environment setting still
    /// supplies DataMask=0, because live dispatch needs position, ETA and
    /// movement evidence.
    /// </summary>
    public int DataMask
    {
        get => _dataMask;
        set => _dataMask = value | 0x01 | 0x04;
    }

    /// <summary>RoadTech flag to include active vehicles only.</summary>
    public bool OnlyLive { get; set; } = true;

    /// <summary>Safety limit for RoadTech Offset pagination.</summary>
    public int MaxPages { get; set; } = 100;

    public bool IsConfigured => Enabled && BaseUrlConfigurationError is null &&
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ApiKey) &&
        !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password) &&
        !string.IsNullOrWhiteSpace(CompanyCode);
}
