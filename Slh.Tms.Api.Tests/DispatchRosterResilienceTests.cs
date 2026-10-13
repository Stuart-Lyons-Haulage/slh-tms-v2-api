using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class DispatchRosterResilienceTests
{
    [Fact]
    public void Dispatch_roster_caches_last_good_result_and_fail_softens_optional_feeds()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "Services")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var source = File.ReadAllText(Path.Combine(directory!.FullName, "Services", "DispatchService.cs"));
        Assert.Contains("dispatch-driver-roster:", source);
        Assert.Contains("cache.Set(cacheKey, result, TimeSpan.FromSeconds(15))", source);
        Assert.Contains("Shared Driver Availability was unavailable", source);
        Assert.Contains("return cachedRoster ?? Array.Empty<DispatchDriverDto>();", source);
    }
}
