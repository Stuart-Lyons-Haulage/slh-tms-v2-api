namespace Slh.Tms.Api.Models.Integrations;

public sealed class SageHrOptions
{
    public string BaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public string DriverTeamName { get; set; } = "Drivers";
    public string DriverPositionKeyword { get; set; } = "Driver";
}
