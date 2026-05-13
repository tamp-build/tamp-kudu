# Changelog

All notable changes to **Tamp.Kudu** are recorded here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versions follow [SemVer](https://semver.org/spec/v2.0.0.html).

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
