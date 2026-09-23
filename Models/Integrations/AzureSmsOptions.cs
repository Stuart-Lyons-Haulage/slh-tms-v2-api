namespace Slh.Tms.Api.Models.Integrations;

public sealed class AzureSmsOptions
{
    public string? ConnectionString { get; set; }
    public string? From { get; set; }
    public bool Enabled { get; set; }
}
