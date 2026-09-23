# SLH TMS API Production Deployment Checklist

This document defines the mandatory checks and procedures required before deploying to production.

## Pre-Deployment Requirements

### 1. Code Review & CI Status
- [ ] Pull request has 2+ approved reviews
- [ ] All GitHub Actions workflows **PASSED** (green checkmark)
  - [ ] API Build
  - [ ] Scheduled Jobs Build
  - [ ] Power Automate info-mailbox intake validation
  - [ ] Power Automate customer load-plan outbound validation
  - [ ] xUnit Tests (all 659 tests passing)
  - [ ] CodeQL Security Analysis
- [ ] No merge conflicts or failing status checks
- [ ] Branch is up-to-date with `main`

### 2. Database Readiness
- [ ] SQL migrations reviewed and tested in dev/staging
- [ ] BACPAC backup taken before deployment window
- [ ] Schema changes do not break current running queries
- [ ] Connection strings and Key Vault references verified
- [ ] Run `GET /api/v1/diagnostics/data-readiness` in staging – all checks green

### 3. Configuration Verification
- [ ] Entra tenant ID, audience, allowed domains correct for environment
- [ ] CORS allowed origins updated if changed
- [ ] All provider credentials (RoadTech, TachoMaster, Fleetio, Sage HR) in Key Vault, not in code
- [ ] Deployment revision number incremented in configuration
- [ ] Feature flags/toggles reviewed and set correctly

### 4. Security Checklist
- [ ] No secrets, connection strings, or API keys in repository
- [ ] CodeQL found no new critical/high vulnerabilities
- [ ] Third-party dependencies checked with `dotnet list package --vulnerable`
- [ ] JwtBearer, ASP.NET Core, and EF Core security patches current
- [ ] Azure OIDC deployment identity has minimum required permissions

### 5. Stakeholder Sign-Off
- [ ] Product Owner approves feature/fix
- [ ] Operations team notified of deployment window
- [ ] Support team has updated runbooks if customer-facing behavior changed
- [ ] Monitoring/alerting rules reviewed and active

---

## Deployment Steps

### Step 1: Build & Test Image
```bash
# GitHub Actions does this automatically for you
# Verify in Actions tab that the Docker image built successfully
```

### Step 2: Deploy to Azure Container Apps
```bash
# Deployment via GitHub Actions using Azure OIDC
# Monitor the deployment job in the Actions tab
# Expected duration: 3-5 minutes
```

### Step 3: Immediate Post-Deployment Checks

Run these within 2 minutes of deployment:

```bash
# Check API is running
curl -s https://slh-tms-api-prod.gentlepond-08dba66b.uksouth.azurecontainerapps.io/api/v1/health \
  -H "Authorization: Bearer <valid-token>" | jq .

# Expected response:
# {
#   "status": "Healthy",
#   "deployment": {
#     "revision": "<NEW_VERSION>"
#   }
# }
```

### Step 4: Health & Readiness Verification

Run these checks and verify all return green:

```bash
# Basic health
GET /api/v1/health
# Expected: { "status": "Healthy" }

# Readiness (dependencies)
GET /api/v1/health/ready
# Expected: { "status": "Ready" }

# Database connectivity
GET /api/v1/diagnostics/data-readiness
# Expected: All checks Passed

# Provider status (RoadTech, TachoMaster, Fleetio, Sage HR)
GET /api/v1/integrations/status
# Expected: Connected or Safe-to-Proceed

# Tracking data freshness
GET /api/v1/health/tracking
# Expected: Recent vehicle locations, not stale

# TachoMaster integration
GET /api/v1/health/tachomaster
# Expected: Card and duty evidence current

# Geofence coverage
GET /api/v1/health/geofences
# Expected: 314+ fences loaded, checksum valid
```

### Step 5: Smoke Tests

Perform these manual checks against live production:

1. **Order Intake**
   - [ ] Send test email to info@lyonshaulage.com
   - [ ] Verify order appears in PendingReview within 2 minutes
   - [ ] Verify source evidence (sender, subject, body) is retained

2. **Planner Import**
   - [ ] Upload a small test plan via `POST /api/v1/planning/import-plan`
   - [ ] Verify runs are created with correct dates and allocations

3. **Driver Dispatch**
   - [ ] Generate a customer load-plan PDF
   - [ ] Verify formatting, route info, and allocations are correct

4. **Run Timing / ETA**
   - [ ] Query live runs: `GET /api/v1/runs?date=<today>&loadId=<test-load>`
   - [ ] Verify ETA, tracking state, and driver status appear

5. **TV Wallboard Feed**
   - [ ] Test paired TV wallboard with `X-TV-Display-Key` header
   - [ ] Verify delivery ETAs and progress are visible

### Step 6: Monitor for 30 Minutes

- [ ] Watch Azure Container Apps metrics (CPU, memory, request rate)
- [ ] Check Application Insights for exceptions or slow requests
- [ ] Monitor email intake queue – no stuck messages
- [ ] No spike in HTTP 5xx errors

### Step 7: Finalize Deployment

- [ ] Log deployment completion with timestamp and deployed revision
- [ ] Tag git commit with release version: `git tag v<semver> && git push origin v<semver>`
- [ ] Post deployment summary to team chat
- [ ] Update status page / internal comms if customer-facing

---

## Rollback Procedure

If critical issues are discovered within 30 minutes of deployment:

### Immediate Action

```bash
# Azure CLI rollback (redeploy previous revision)
az containerapp update \
  --name slh-tms-api-prod \
  --resource-group slh-tms-prod \
  --set-traffic <previous-revision-name>=100
```

### Post-Rollback

1. Verify health endpoint shows previous revision
2. Run smoke tests again
3. Notify team of rollback reason
4. Create incident report
5. Do not re-attempt deployment until root cause is identified

---

## Deployment Window Best Practices

- **Preferred time:** Off-peak hours (after 18:00 UK time)
- **Avoid:** Friday afternoons, holiday periods
- **Communication:** Notify ops team 24 hours in advance
- **Team availability:** Ensure someone can monitor for 2 hours post-deployment
- **Escalation:** Have rollback authority on standby

---

## Post-Deployment: Next 24 Hours

- [ ] No elevated error rates in Application Insights
- [ ] Database backups completed successfully
- [ ] Office-server BACPAC backup job succeeded
- [ ] No customer reports of issues
- [ ] TMS wallboards and portals operating normally

---

## Emergency Contacts

| Role | Contact | Escalation |
|------|---------|------------|
| On-Call DevOps | [TMS] Slack channel | Team Lead |
| Database Admin | [TMS] Slack channel | CTO |
| Support Lead | support@lyonshaulage.com | Operations Manager |

---

## References

- [Azure Container Apps Deployment Docs](https://learn.microsoft.com/en-us/azure/container-apps/)
- [GitHub Actions OIDC](https://docs.github.com/en/actions/deployment/security-hardening-your-deployments/about-security-hardening-with-openid-connect)
- [SLH TMS API Architecture](./README.md)
- [SQL Backup & Disaster Recovery](./ops/backup/README.md)
