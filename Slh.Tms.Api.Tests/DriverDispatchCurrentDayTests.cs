using System.Reflection;
using Slh.Tms.Api.Controllers;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class DriverDispatchCurrentDayTests
{
    [Fact]
    public void Controller_no_longer_defaults_to_tomorrow()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Controllers", "DriverDispatchController.cs"));
        Assert.Contains("var planningDate = date ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, London).DateTime);", source);
        Assert.DoesNotContain("DateTime).AddDays(1)", source);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Controllers"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Repository root was not found.");
    }
}
