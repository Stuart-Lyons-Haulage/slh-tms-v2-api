using System.Text.Json.Nodes;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class StagedOrderPayloadAmendmentTests
{
    [Fact]
    public void ReviewFieldAliasesAreCopiedToPromotionFields()
    {
        var payload = JsonNode.Parse("""
            {
              "collectionSiteName": "NWF-Merston",
              "deliverySiteName": "Morrisons-Stockton",
              "pallets": 7,
              "temperature": "+3°C",
              "orderType": "NWF inbound",
              "notes": "Call one hour before delivery",
              "sourceLines": [ { "collectionSite": "Old collection", "deliverySite": "Old delivery" } ]
            }
            """)!.AsObject();

        StagedOrderPayloadAmendment.Apply(payload);

        Assert.Equal("NWF-Merston", payload["collectionLocation"]?.GetValue<string>());
        Assert.Equal("NWF-Merston", payload["sellerName"]?.GetValue<string>());
        Assert.Equal("Morrisons-Stockton", payload["deliveryLocation"]?.GetValue<string>());
        Assert.Equal("Morrisons-Stockton", payload["stallNumber"]?.GetValue<string>());
        Assert.Equal("+3°C", payload["temperatureRequirement"]?.GetValue<string>());
        Assert.Equal("NWF inbound", payload["jobType"]?.GetValue<string>());
        Assert.Equal("Call one hour before delivery", payload["driverInstructions"]?.GetValue<string>());

        var sourceLine = payload["sourceLines"]![0]!.AsObject();
        Assert.Equal("NWF-Merston", sourceLine["collectionSite"]?.GetValue<string>());
        Assert.Equal("Morrisons-Stockton", sourceLine["deliverySite"]?.GetValue<string>());
    }
}
