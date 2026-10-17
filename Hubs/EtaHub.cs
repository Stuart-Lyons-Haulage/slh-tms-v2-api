using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Slh.Tms.Api.Hubs;

[Authorize]
public sealed class EtaHub : Hub;
