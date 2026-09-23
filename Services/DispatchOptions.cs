namespace Slh.Tms.Api.Services;

public sealed class DispatchOptions
{
    public decimal NorthernLatitudeThreshold { get; set; } = 52.5m;
    public decimal HomeLatitude { get; set; } = 50.84m;
    public decimal HomeLongitude { get; set; } = -0.64m;
    public decimal BackloadRadiusMiles { get; set; } = 80m;
    public decimal SouthboundMinimumLatitudeDrop { get; set; } = 0.20m;
    public int TachoHistoryDays { get; set; } = 14;
}
