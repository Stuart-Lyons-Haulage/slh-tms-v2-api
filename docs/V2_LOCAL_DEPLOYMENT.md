# SLH TMS V2 local server and VPN deployment

## Goal

Provide a simple, business-only deployment that works quickly on the Lyons office network, remains reachable by authorised staff outside the office and can still communicate with external APIs.

The design intentionally avoids Azure as a runtime dependency.

## Recommended topology

```text
                           Internet
                              |
          +-------------------+-------------------+
          |                                       |
   External APIs                         Authorised remote staff
 Microsoft Graph                              phone/laptop
 TachoMaster                                      |
 RoadTech                                         |
 Sage HR                                          VPN
 Other providers                                  |
          |                                       |
          +------------- outbound HTTPS ----------+
                              |
                      Lyons office network
                              |
                    +----------------------+
                    |   SLH TMS V2 Server  |
                    |----------------------|
                    | Web portal           |
                    | ASP.NET V2 API       |
                    | Background workers   |
                    | SQL Server           |
                    | Backup agent         |
                    +----------------------+
                              |
                  Office PCs / TV / planners
```

## Host

Use a dedicated always-on Windows machine, not an employee's everyday workstation.

Initial practical specification:
- Windows 11 Pro or Windows Server
- 16 GB RAM minimum; 32 GB preferred
- modern 4+ core CPU
- 512 GB+ SSD
- wired Ethernet
- UPS
- automatic restart after power recovery

The application code must remain portable so a stronger server or cloud host can replace this machine later without redesigning the domain.

## VPN

The application must not be directly exposed to the public internet.

For the first deployment, use a WireGuard-based VPN. Tailscale is a suitable low-administration implementation, but V2 must not depend on Tailscale-specific application code.

Expected behaviour:
- server joins the private VPN
- each authorised staff device joins the same private network
- remote staff reach the same private portal/API address as office users where practical
- access can be revoked per device/user
- SQL Server is not reachable from general remote clients unless specifically required

A future firewall/router-hosted WireGuard deployment can replace the first VPN without changing the TMS.

## Internal addressing

Preferred office name:
- `http://slh-tms` during initial local testing
- move to HTTPS internally before production cutover

API:
- portal should normally call a same-origin path such as `/api/v2`
- avoid separate public API URLs
- a reverse proxy on the server can route portal and API traffic internally

This removes most CORS and public URL complexity.

## Authentication

VPN access controls network reachability; application authentication still controls user identity and permissions.

Recommended layers:
1. VPN authorises the device/user onto the private network.
2. TMS sign-in authorises the person inside the application.
3. role-based permissions control Admin, Planner, Operations, Read Only and TV/display access.

Existing Microsoft Entra sign-in can be retained later if useful, but the system must remain operable without Azure-hosted application infrastructure.

## API integrations

Prefer outbound connections initiated by the TMS server:
- Microsoft Graph / Outlook: direct Info mailbox polling for order intake
- RoadTech: one configured integration source; Falcon supplies tracking/execution evidence and TachoMaster supplies card/duty/legal-hours evidence
- Sage HR: outbound workforce sync
- Roadrunner: file/API export as supported

### Info mailbox intake through Microsoft Graph

The canonical V2 deployment polls `info@lyonshaulage.com` directly from the API host. This is the preferred inbound order-intake path for an always-on local server and does not require the planner PC or a public inbound API endpoint.

The poller reuses the canonical `/api/v1/order-intake/email` logic, so parsing, duplicate handling, retained evidence and Order Review staging remain aligned.

Set these values in server/container secret configuration, never in Git:

```text
Integrations__InfoMailboxGraph__Enabled=true
Integrations__InfoMailboxGraph__TenantId=<Entra tenant id>
Integrations__InfoMailboxGraph__ClientId=<Entra app registration client id>
Integrations__InfoMailboxGraph__ClientSecret=<secret>
Integrations__InfoMailboxGraph__Mailbox=info@lyonshaulage.com
```

The Entra app requires Microsoft Graph application permission `Mail.Read`, admin consent and mailbox-restricted application access. Verify `GET /api/v1/health/intake` reports a recent successful poll before retiring the previous Power Automate intake path. Authorised operators can also trigger `POST /api/v1/health/intake/poll`.

During cutover, do not run two competing intake paths continuously. Keep the previous flow only as a stopped/reversible fallback until direct Graph polling is verified.

This works normally behind the office firewall because the server initiates the HTTPS connection.

## Inbound webhooks

Do not publish the full V2 API just to receive one webhook.

If a provider genuinely requires inbound delivery:
1. create a dedicated route such as `/integration/<provider>/webhook`
2. require provider authentication/signature verification
3. expose only that route through a secure tunnel/reverse proxy
4. log every accepted/rejected request
5. rate-limit it
6. never expose SQL Server
7. never expose admin or planner endpoints through the tunnel

Where polling is available and operationally sensible, prefer polling over public ingress.

## SQL Server

Use a separate V2 database.

Initial database name:
- `SLH_TMS_V2`

Schemas:
- `master`
- `intake`
- `ops`
- `live`
- `integration`

Rules:
- local/VPN-only network access
- least-privilege application login
- migrations executed by the V2 deployment process
- never point V2 migrations at the V1 database

## Service hosting

Preferred production model on Windows:
- API/background worker as an auto-starting service or container
- portal served by the local reverse proxy
- SQL Server as the local database service
- all services restart automatically after reboot
- health checks used by the local supervisor

The first release should optimise for reliability and simple support rather than elaborate orchestration.

## Backups

Minimum production backup policy:
- nightly SQL full backup
- frequent transaction/log backup if the chosen SQL edition/recovery model supports it and the business needs it
- encrypted off-machine copy
- retain multiple generations
- monthly restore test during early rollout
- application configuration backup without plaintext credentials

A backup that has never been restored is not considered proven.

## Secrets

Do not store API keys, passwords or connection strings in GitHub.

Use protected local configuration:
- Windows credential protection / service account configuration
- environment variables or encrypted secret store
- restricted NTFS permissions

Integration credentials are provided only at deployment time.

## GitHub CI/CD

GitHub remains private and authoritative for source code.

CI should:
- restore/build API
- run unit/integration tests
- build portal
- produce versioned release artifacts

Deployment to the local server can initially be a controlled release step. Once stable, add a self-hosted GitHub Actions runner on the server or a separate deployment machine so approved V2 releases can be installed automatically.

The runner must not be used to execute unreviewed arbitrary code from public forks.

## Cutover

1. Build V2 while V1 stays live.
2. Install V2 on the local server.
3. Load clean canonical Master Data.
4. Run intake/order/planning flows side-by-side.
5. Test office LAN access.
6. Test VPN remote access.
7. Test each external integration.
8. Prove backup + restore.
9. Agree a cutover date.
10. Keep V1 available for rollback during the stability window.
11. Retire V1 only after V2 has passed the agreed period.

## Manual actions expected from the user

The user should only need to help with:
- choosing/providing the dedicated host
- administrator login to that host
- approving the VPN client/account
- installing VPN on staff devices that need remote access
- authorising Microsoft/integration accounts when prompted
- selecting the off-machine backup destination

Everything else should be encoded in the V2 repository and deployment scripts as far as practical.
