using System.Text.Json;
using System.Text.Json.Nodes;
using Slh.Tms.Api.Controllers;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class MasterDataReconcileMergeTests
{
    [Fact]
    public void Blank_uploaded_fields_do_not_erase_existing_protected_values()
    {
        var current = JsonNode.Parse("""{"registration":"BL70RHU","fuelPin":"3131","bpPlainCard":"YES","shellCard":"YES","notes":"Keep me"}""")!.AsObject();
        using var document = JsonDocument.Parse("""{"registration":"BL70RHU","fuelPin":"","bpPlainCard":"","notes":"Updated note"}""");

        var merged = MasterDataReconcileMerge.Merge(current, document.RootElement);

        Assert.Equal("3131", merged["fuelPin"]?.ToString());
        Assert.Equal("YES", merged["bpPlainCard"]?.ToString());
        Assert.Equal("YES", merged["shellCard"]?.ToString());
        Assert.Equal("Updated note", merged["notes"]?.ToString());
    }

    [Fact]
    public void Explicit_false_and_zero_are_real_updates()
    {
        var current = JsonNode.Parse("""{"active":true,"standardCapacity":26}""")!.AsObject();
        using var document = JsonDocument.Parse("""{"active":false,"standardCapacity":0}""");

        var merged = MasterDataReconcileMerge.Merge(current, document.RootElement);

        Assert.Equal("false", merged["active"]?.ToJsonString());
        Assert.Equal("0", merged["standardCapacity"]?.ToJsonString());
    }
}
