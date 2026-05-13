# Changelog

All notable changes to **Tamp.Kudu** are recorded here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versions follow [SemVer](https://semver.org/spec/v2.0.0.html).

## [0.2.2] - 2026-05-13

### Fixed

- **`ManagementClient.GetConfigReferencesAsync` was deserializing against the wrong response shape.**
  The 0.2.1 URL fix unblocked the request but the model expected
  `{ properties: { keyToReferenceStatuses: {...} } }` (a dict at the leaf) while ARM actually
  returns `{ value: [{ id, name, location, properties: {...}, type }, ...] }` (a resource list).
  The deserializer didn't fail; it just produced an empty dictionary — same silent-zero failure
  family as the original URL bug. Caught by strata-scott against `strata-api-dev` immediately
  after the 0.2.1 bump.

  Fix: deserialize into a new public `ConfigReferencesRawResponse` then project into the existing
  dict-shaped `ConfigReferencesResponse` keyed by `entry.Name`. Public API unchanged — adopter
  code still uses `refs.Properties.KeyToReferenceStatuses[settingName]`. Adopters who want the
  full resource metadata (id, location, type) can call `GetAsync<ConfigReferencesRawResponse>(...)`
  directly via the protected base methods.

### Added

- **`ManagementClient.GetConfigReferenceAsync(settingName)`** — single-resource getter for the
  per-setting endpoint `/config/configreferences/appsettings/{settingName}`. Returns the
  `ConfigReferenceStatus` directly (null on 404). Useful when you have a specific setting in
  hand and don't want to round-trip the whole list.

- **`ConfigReferenceStatus.ActiveVersion`** — the currently-resolved version of the KV secret
  (the GUID portion of the active reference URI). Surfaces from ARM but wasn't in the prior
  model. Useful for rotation tracking when a setting points at a versionless KV reference.

- **`ConfigReferencesRawResponse`** + **`ConfigReferenceResourceEntry`** — public raw-shape
  types matching ARM's wire format. Exposed so adopters can bypass the dict projection when
  they need the full resource metadata.

## [0.2.1] - 2026-05-13

### Fixed

- **`ManagementClient.GetConfigReferencesAsync` was emitting the wrong ARM URL** — bug present
  in both 0.1.0 and 0.2.0. The endpoint lives at `/sites/{}/config/configreferences/appsettings`
  (alongside the rest of the `WebApps_*-Configurations` family — `/config/appsettings/list`,
  `/config/connectionstrings/list`); the implementation was missing the `/config/` segment and
  hitting `/sites/{}/configreferences/appsettings`, which ARM 404s.

  Symptom on adopters: `ApiClientException: GET ... -> 404 Not Found` from any
  `CheckKvRefs`-style diagnostic target. Other ManagementClient endpoints (`/stop`, `/start`,
  `/restart`, `/config/appsettings/list`, `/config/publishingcredentials/list`) were correct
  — this was the lone misshapen URL.

  Caught by strata-scott against `strata-api-dev` shortly after wiring `CheckKvRefs` into the
  build script under Tamp.Core 1.5.0. Test now pins the correct path
  (`/config/configreferences/appsettings`) as a regression fence.

## [0.2.0] - 2026-05-13

### Added — DeploymentClient (TAM-184)

- **`KuduClient.Deployment`** — new sub-client wrapping `/api/zipdeploy` plus the
  `/api/deployments` introspection routes. The canonical Azure App Service deploy path.

- **`DeploymentClient.ZipDeployAsync(zipPath, async?, pollInterval?, timeout?, ct?)`** —
  zip-deploy from a file path. Streams as raw body to `/api/zipdeploy` with
  `Content-Type: application/zip`.

  - `async: false` (default) — Kudu holds the connection open until provisioning completes;
    suitable for small zips and fast provisioning.
  - `async: true` — wrapper posts with `?isAsync=true`, captures the deployment ID from the
    `Location` header, polls `/api/deployments/{id}` until terminal state (status 0 = Success,
    1 = Failed) or `timeout` (default 10 min) elapses.

- **`DeploymentClient.ZipDeployAsync(Stream, ...)`** — same flow for in-memory zips.

- **`DeploymentClient.GetStatusAsync(deploymentId)`** — typed `DeploymentEntry?` for a specific
  deployment. Returns null on 404.

- **`DeploymentClient.ListAsync()`** — recent deployments.

- **`ZipDeployResult`** — `IsSuccess` / `DeploymentId` / `Status` / `StatusCode` / `LogText`.
  Status values: `Success`, `Failed`, `TimedOut`.

- **`DeploymentEntry`** — JSON-mapped record for the `/api/deployments` shape. Numeric
  `Status` field documented inline (0 Success, 1 Failed, 2 Pending, 3 Building, 4 Deploying).

### Notes

- Driven by strata-scott's 2026-05-13 universal-friction report. Every Azure App Service deploy
  step in any Tamp adopter's pipeline either zips + zip-deploys or shells out to
  `az webapp deploy --type zip` (same endpoint underneath). Pre-0.2.0 Strata had to keep using
  either the `AzureWebApp@1` ADO task or a manual `az` shell-out for DeployApi.

- **Zip creation is project-side.** Adopters use `System.IO.Compression.ZipFile.CreateFromDirectory`
  or any other zip producer — Tamp does not opine on the layout (per the universal-design rule).

- 10 new unit tests; 33/33 green across net8/9/10. Test seam: `HttpMessageHandler` shims for
  both `RecordingHandler` and `ScriptedHandler` exercise the POST → 202 → poll → terminal-state
  cycle deterministically without real Kudu.

## [0.1.0] - 2026-05-13

### Added

- Initial release. Two clients covering the App Service operational surface adjacent to
  Kudu. Filed under TAM-173.

- **`KuduClient`** at `https://<site>.scm.azurewebsites.net/` with HTTP Basic auth from
  publishing credentials. Sub-clients:
  - `Vfs` — `UploadAsync`, `UploadTextAsync`, `DownloadAsync`, `ListAsync`, `DeleteAsync`.
    URL construction bakes in the `/home` vfs-root convention; `If-Match: "*"` is set
    automatically on PUT/DELETE for overwrite semantics.
  - `Command` — `ExecuteAsync` for inline strings, plus `ExecuteBashScriptAsync` that
    uploads a `.sh` file via vfs and invokes it via `bash /home/...` in one call (the
    recommended pattern for non-trivial scripts).
  - `Settings` — Kudu-flavored app settings (`/api/settings`); does NOT resolve KV refs.

- **`ManagementClient`** at `https://management.azure.com/` with Bearer auth (ARM token).
  Covers App Service operations that aren't on the Kudu surface:
  - Lifecycle: `StartAsync`, `StopAsync`, `RestartAsync`.
  - `ListPublishingCredentialsAsync` returns the publishing-profile username + password
    + SCM URI; downstream callers wrap the password in a `Secret` at the boundary.
  - `GetConfigReferencesAsync` returns the `configreferences/appsettings` payload —
    the most useful diagnostic when a KV reference isn't resolving (per Strata's
    STRATA-453 incident). The `ConfigReferenceStatus` record surfaces vault name,
    secret name, identity type, and a `Details` string with the resolution error.
  - `ListAppSettingsAsync` / `SetAppSettingsAsync` — the Management-side appsettings
    route, which resolves KV references (unlike the Kudu side).

### Notes

- Driven by Strata's adoption-wave gap list 2026-05-13 (P0 priority — multiple sessions
  spent doing Kudu ops by hand: STRATA-356, STRATA-453, migration-runner bring-up).
  Pinned to `Tamp.Core` / `Tamp.NetCli.V10` at 1.4.1 (the version whose
  `InternalsVisibleTo` list includes `Tamp.Kudu`).
