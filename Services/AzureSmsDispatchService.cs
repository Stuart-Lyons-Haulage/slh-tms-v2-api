using System.Text.RegularExpressions;
using Azure.Communication.Sms;
using Slh.Tms.Api.Models.Integrations;

namespace Slh.Tms.Api.Services;

public sealed class AzureSmsDispatchService(AzureSmsOptions options, ILogger<AzureSmsDispatchService> logger)
{
    private static readonly Regex E164 = new(@"^\+[1-9]\d{7,14}$", RegexOptions.Compiled);

    public bool IsConfigured => options.Enabled && !string.IsNullOrWhiteSpace(options.ConnectionString) && !string.IsNullOrWhiteSpace(options.From);

    public async Task<SmsDispatchResult> SendAsync(string destination, string message, CancellationToken ct)
    {
        if (!IsConfigured) throw new InvalidOperationException("Azure SMS delivery is not configured for this environment.");
        if (!E164.IsMatch(destination)) throw new InvalidOperationException("The assigned driver mobile number must use E.164 format, for example +447700900123.");

        var client = new SmsClient(options.ConnectionString);
        var response = await client.SendAsync(options.From!, destination, message, new SmsSendOptions(enableDeliveryReport: true), ct);
        var result = response.Value;
        if (!result.Successful) throw new InvalidOperationException(result.ErrorMessage ?? "Azure SMS did not accept the driver dispatch.");
        logger.LogInformation("Sent dispatch SMS {MessageId} to driver mobile ending {MobileSuffix}", result.MessageId, destination[^4..]);
        return new SmsDispatchResult(result.MessageId, destination[^4..], "Azure SMS");
    }
}
