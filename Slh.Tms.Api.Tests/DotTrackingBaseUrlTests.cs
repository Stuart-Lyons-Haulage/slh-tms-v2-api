using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class DotTrackingBaseUrlTests
{
    [Theory]
    [InlineData("api-v1.roadtech.co.uk", "https://api-v1.roadtech.co.uk")]
    [InlineData("https://api-v1.roadtech.co.uk", "https://api-v1.roadtech.co.uk")]
    [InlineData("https://api-v1.roadtech.co.uk/", "https://api-v1.roadtech.co.uk")]
    public void Options_normalise_supported_runtime_base_urls(string input, string expected)
    {
        var options = new DotTrackingOptions { BaseUrl = input };

        Assert.Equal(expected, options.BaseUrl);
        Assert.Null(options.BaseUrlConfigurationError);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://api-v1.roadtech.co.uk")]
    public void Options_reject_invalid_runtime_base_urls_without_throwing(string input)
    {
        var options = new DotTrackingOptions { BaseUrl = input, Enabled = true };

        Assert.Equal(string.Empty, options.BaseUrl);
        Assert.NotNull(options.BaseUrlConfigurationError);
        Assert.False(options.IsConfigured);
    }

    [Fact]
    public void Client_normaliser_appends_api_path_after_safe_option_normalisation()
    {
        var options = new DotTrackingOptions { BaseUrl = "api-v1.roadtech.co.uk" };

        Assert.Equal("https://api-v1.roadtech.co.uk/api/", DotTrackingClient.NormaliseBaseUrl(options.BaseUrl));
    }
}
