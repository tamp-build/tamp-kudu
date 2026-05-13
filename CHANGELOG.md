# Changelog

All notable changes to **Tamp.Kudu** are recorded here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versions follow [SemVer](https://semver.org/spec/v2.0.0.html).

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
