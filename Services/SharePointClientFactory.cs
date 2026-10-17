using System.Net.Http;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Creates the named Microsoft Graph HTTP client used by SharePoint integrations.
/// Authentication remains owned by the archive service so token acquisition and
/// retry/observability behavior stay in one place.
/// </summary>
public sealed class SharePointClientFactory(IHttpClientFactory httpClientFactory)
{
    public HttpClient CreateClient() =>
        httpClientFactory.CreateClient("SharePointOrderArchive");
}
