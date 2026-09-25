using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class MasterDataDuplicateReviewResilienceTests : IClassFixture<CustomWebFactory>
{
    private const string LyonsUser = "planner@lyonshaulage.com";
    private readonly CustomWebFactory _factory;

    public MasterDataDuplicateReviewResilienceTests(CustomWebFactory factory) => _factory = factory;

    [Fact]
    public async Task Site_scan_surfaces_same_external_code_when_address_is_missing()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.Sites.AddRange(
                new Site { ExternalCode = $"DUP{suffix}", Name = $"NWF Site {suffix}", Active = true },
                new Site { ExternalCode = $"DUP{suffix}", Name = $"NWF Site {suffix}", DriverTextName = $"NWF Site {suffix}", Active = true });
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClientWithUser(LyonsUser);
        var response = await client.GetAsync("/api/v1/operational-master-data/duplicates?entityType=sites");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var candidates = await response.Content.ReadFromJsonAsync<List<MasterDataDuplicateCandidate>>();
        var candidate = Assert.Single(candidates!.Where(x => x.Canonical.Code == $"DUP{suffix}"));
        Assert.True(candidate.CanAutoMerge);
        Assert.True(candidate.Confidence >= 95);
    }

    [Fact]
    public async Task Site_auto_merge_accepts_same_external_code_candidates_scored_for_operational_review()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.Sites.AddRange(
                new Site { ExternalCode = $"SAFE{suffix}", Name = $"Natures Way Selsey {suffix}", CollectionAddress = "Selsey PO20 9HP", Active = true },
                new Site { ExternalCode = $"SAFE{suffix}", Name = $"Natures Way Foods {suffix}", MapLink = "https://maps.example/selsey", Active = true });
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClientWithUser(LyonsUser);
        var response = await client.PostAsync("/api/v1/operational-master-data/duplicates/auto-merge?entityType=sites", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<MasterDataDuplicateMergeResult>();
        Assert.True(result!.Merged >= 1);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<TmsDbContext>();
        Assert.Single(verifyDb.Sites.Where(site => site.ExternalCode == $"SAFE{suffix}" && site.Active));
    }

    [Fact]
    public async Task Driver_scan_keeps_employee_number_only_duplicates_for_review()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.Drivers.AddRange(
                new Driver { EmployeeNumber = $"EMP{suffix}", DisplayName = $"Driver {suffix}", Active = true },
                new Driver { EmployeeNumber = $"EMP{suffix}", DisplayName = $"Driver {suffix}", TachoMasterDriverId = $"TM{suffix}", Active = true });
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClientWithUser(LyonsUser);
        var response = await client.GetAsync("/api/v1/operational-master-data/duplicates?entityType=drivers");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var candidates = await response.Content.ReadFromJsonAsync<List<MasterDataDuplicateCandidate>>();
        var candidate = Assert.Single(candidates!.Where(x => x.Canonical.Code == $"EMP{suffix}"));
        Assert.False(candidate.CanAutoMerge);
        Assert.Equal(88, candidate.Confidence);
    }

    [Fact]
    public async Task Trailer_scan_matches_slh_numeric_aliases()
    {
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.Trailers.AddRange(
                new Trailer { TrailerNumber = "1", Type = "Urban", Active = true },
                new Trailer { TrailerNumber = "SLH001", StandardCapacity = 26, Active = true });
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClientWithUser(LyonsUser);
        var response = await client.GetAsync("/api/v1/operational-master-data/duplicates?entityType=trailers");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var candidates = await response.Content.ReadFromJsonAsync<List<MasterDataDuplicateCandidate>>();
        Assert.Contains(candidates!, x => x.Canonical.Code is "1" or "SLH001" && x.Duplicates.Any(row => row.Code is "1" or "SLH001"));
    }

    [Fact]
    public async Task Market_auto_merge_accepts_same_market_stand_candidates_without_sender()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.MarketContacts.AddRange(
                new MarketContact { Market = "Covent", Name = $"Berry Seller {suffix}", StandOrLocation = "A12", Active = true },
                new MarketContact { Market = "Covent", Name = $"Berry Seller {suffix}", StandOrLocation = "A12", Salesman = "Night sales", Active = true });
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClientWithUser(LyonsUser);
        var response = await client.PostAsync("/api/v1/operational-master-data/duplicates/auto-merge?entityType=markets", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<MasterDataDuplicateMergeResult>();
        Assert.True(result!.Merged >= 1);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<TmsDbContext>();
        Assert.Single(verifyDb.MarketContacts.Where(contact => contact.Name == $"Berry Seller {suffix}" && contact.Active));
    }

    [Fact]
    public async Task Vehicle_merge_reassigns_live_loads_and_integration_mappings()
    {
        var canonicalId = Guid.NewGuid();
        var duplicateId = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.Vehicles.AddRange(
                new Vehicle { Id = canonicalId, Registration = $"AB{suffix}", FleetNumber = "Fleet 1", Active = true },
                new Vehicle { Id = duplicateId, Registration = $"AB{suffix}", FuelPin = "1234", Active = true });
            db.Loads.Add(new Load { Reference = $"RUN{suffix}", PlanningDate = DateOnly.FromDateTime(DateTime.UtcNow), VehicleId = duplicateId });
            db.IntegrationMappings.Add(new IntegrationMapping { Provider = "Fleetio", ExternalKey = $"fleetio-{suffix}", TmsEntityType = "Vehicle", TmsEntityId = duplicateId, Active = true });
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClientWithUser(LyonsUser);
        var response = await client.PostAsJsonAsync("/api/v1/operational-master-data/duplicates/vehicles/merge", new MasterDataDuplicateMergeRequest(canonicalId, [duplicateId], "test vehicle merge"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var canonical = await verifyDb.Vehicles.FindAsync(canonicalId);
        var duplicate = await verifyDb.Vehicles.FindAsync(duplicateId);
        Assert.Equal("1234", canonical!.FuelPin);
        Assert.False(duplicate!.Active);
        Assert.All(verifyDb.Loads.Where(load => load.Reference == $"RUN{suffix}"), load => Assert.Equal(canonicalId, load.VehicleId));
        Assert.All(verifyDb.IntegrationMappings.Where(mapping => mapping.ExternalKey == $"fleetio-{suffix}"), mapping => Assert.Equal(canonicalId, mapping.TmsEntityId));
    }

    [Fact]
    public void Duplicate_scan_has_live_data_resilience_and_explicit_failure_contract()
    {
        var serviceSource = ReadRepoFile("Services/MasterDataDuplicateReviewService.cs");
        var controllerSource = ReadRepoFile("Controllers/MasterDataDuplicateReviewController.cs");

        Assert.Contains("Master-detail enrichment adds", serviceSource, StringComparison.Ordinal);
        Assert.Contains("Fall back to persisted Member Code / employee identity", serviceSource, StringComparison.Ordinal);
        Assert.Contains("operational Master Data duplicate check unavailable", serviceSource, StringComparison.Ordinal);
        Assert.Contains("master_duplicate_scan_failed", controllerSource, StringComparison.Ordinal);
        Assert.Contains("duplicate check could not complete", controllerSource, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reject_endpoint_accepts_frontend_camel_case_payload()
    {
        var client = _factory.CreateClientWithUser(LyonsUser);
        var response = await client.PostAsJsonAsync("/api/v1/operational-master-data/duplicates/reject", new { candidateId = "abc123", entityType = "sites", note = "keep separate test" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static string ReadRepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Slh.Tms.Api.csproj")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        var path = Path.Combine(directory!.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Expected repository file was not found: {path}");
        return File.ReadAllText(path);
    }
}
