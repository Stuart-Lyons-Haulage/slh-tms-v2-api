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

/// <summary>
/// A single cell in a distance matrix: distance and time from origin to destination.
/// </summary>
public sealed record RouteMatrixCell(
    decimal DistanceMetres,
    int DurationSeconds,
    bool IsReachable = true,
    string? FailureReason = null)
{
    /// <summary>
    /// Returns the distance in miles (1 mile = 1609.344 metres).
    /// </summary>
    public decimal DistanceMiles => DistanceMetres / 1609.344m;

    /// <summary>
    /// Returns the duration in minutes.
    /// </summary>
    public int DurationMinutes => DurationSeconds / 60;
}

/// <summary>
/// A complete distance/time matrix from multiple origins to multiple destinations.
/// </summary>
public sealed class RouteMatrix
{
    private readonly Dictionary<(int originIdx, int destIdx), RouteMatrixCell?> _cells = [];

    /// <summary>
    /// Initialize a matrix with dimensions (origins count × destinations count).
    /// </summary>
    public RouteMatrix(int originCount, int destinationCount)
    {
        OriginCount = originCount;
        DestinationCount = destinationCount;
    }

    /// <summary>Number of origins in this matrix.</summary>
    public int OriginCount { get; }

    /// <summary>Number of destinations in this matrix.</summary>
    public int DestinationCount { get; }

    /// <summary>
    /// Get or set a cell value (origin index, destination index).
    /// </summary>
    public RouteMatrixCell? this[int originIndex, int destinationIndex]
    {
        get => _cells.TryGetValue((originIndex, destinationIndex), out var value) ? value : null;
        set => _cells[(originIndex, destinationIndex)] = value;
    }

    /// <summary>
    /// Enumerate all cells in the matrix (origin index, dest index, cell).
    /// </summary>
    public IEnumerable<(int originIdx, int destIdx, RouteMatrixCell? cell)> EnumerateCells()
    {
        for (int o = 0; o < OriginCount; o++)
            for (int d = 0; d < DestinationCount; d++)
                yield return (o, d, this[o, d]);
    }
}
