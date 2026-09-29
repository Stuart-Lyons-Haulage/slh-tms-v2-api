using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class CustomerCodeMatcherTests
{
    [Theory]
    [InlineData("SUMMERBERRY", "SUMMER-BERRY")]
    [InlineData("Covent Garden", "COVENTGARDEN")]
    public void Punctuation_and_spacing_only_do_not_create_a_false_mismatch(string left, string right)
    {
        Assert.True(CustomerCodeMatcher.Equivalent(left, right));
    }

    [Fact]
    public void Different_customer_codes_do_not_match()
    {
        Assert.False(CustomerCodeMatcher.Equivalent("SUMMERBERRY", "MORRISONS"));
    }
}
