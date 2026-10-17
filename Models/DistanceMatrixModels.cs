namespace Slh.Tms.Api.Models;

/// <summary>
/// Geographic coordinate (latitude/longitude pair) for distance matrix operations.
/// </summary>
public sealed record MatrixPoint(decimal Latitude, decimal Longitude)
{
    /// <summary>
    /// Validates that latitude is in range [-90, 90] and longitude is in range [-180, 180].
    /// </summary>
    public void Validate()
    {
        if (Latitude < -90m || Latitude > 90m)
            throw new ArgumentOutOfRangeException(nameof(Latitude), $"Latitude must be between -90 and 90, got {Latitude}");

        if (Longitude < -180m || Longitude > 180m)
            throw new ArgumentOutOfRangeException(nameof(Longitude), $"Longitude must be between -180 and 180, got {Longitude}");
    }
}

/// <summary>
/// Vehicle profile for distance matrix calculations (HGV specs).
/// Used by Samsara integration to get accurate routing for heavy goods vehicles.
/// </summary>
public sealed record HgvVehicleProfile(
    string VehicleType = "HGV",  // HGV, Rigid, Drawbar, Articulated
    decimal HeightMeters = 4.2m,
    decimal WidthMeters = 2.6m,
    decimal LengthMeters = 18.5m,
    decimal WeightTonnes = 44m,
    bool AvoidTolls = false,
    bool AvoidMotorways = false)
{
    /// <summary>Standard HGV (articulated) profile: 44t maximum, 18.5m length.</summary>
    public static readonly HgvVehicleProfile Standard = new();

    /// <summary>Rigid vehicle profile: smaller dimensions, typically 18t.</summary>
    public static readonly HgvVehicleProfile Rigid = new(
        VehicleType: "Rigid",
        HeightMeters: 3.8m,
        LengthMeters: 10.2m,
        WeightTonnes: 18m);
}
