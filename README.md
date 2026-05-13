# Tamp.Kudu

> Typed client for the Azure App Service Kudu REST API + the App Service Management API endpoints adjacent to Kudu operations. Covers the gotcha-prone surfaces: vfs (with the `/home`-root convention), shell command execution (with bash-via-vfs scaffolding), and KV-reference resolution diagnostics.

| Package | Status |
|---|---|
| `Tamp.Kudu` | 0.1.0 (initial) |

## Install

```bash
dotnet add package Tamp.Kudu
```

Multi-targets net8 / net9 / net10. Depends on `Tamp.Http` for the underlying API client + auth + redaction.

## Two clients, two auth modes

`Tamp.Kudu` ships two distinct clients because Kudu and the App Service Management API live at different hosts with different auth contracts:

| Client | Host | Auth | What it does |
|---|---|---|---|
| `KuduClient` | `https://<site>.scm.azurewebsites.net/` | HTTP Basic (publishing-profile creds) | vfs + shell command + Kudu-flavored settings |
| `ManagementClient` | `https://management.azure.com/` | Bearer (ARM access token) | lifecycle (stop/start/restart) + publishing-credential list + KV-reference diagnostics + Management-side appsettings (the route that resolves KV refs) |

Adopters typically use both — `KuduClient` for the SCM-side file-and-script work, `ManagementClient` for the lifecycle and Management-API-only endpoints like `configreferences/appsettings`.

## Quick start — deploy and run a migration

```csharp
using Tamp;
using Tamp.Http;
using Tamp.Kudu;

[Secret("publishing-password", EnvironmentVariable = "KUDU_PASSWORD")]
readonly Secret KuduPassword = null!;

[Parameter] readonly string KuduUser = "$strata-api-dev";

Target ApplyMigration => _ => _.Executes(async () =>
{
    using var kudu = new KuduClient("strata-api-dev",
        ApiCredential.Basic(KuduUser, KuduPassword));

    var migrationScript = File.ReadAllText(RootDirectory / "scripts" / "migrate.sh");
    var result = await kudu.Command.ExecuteBashScriptAsync(
        "migrate.sh",
        migrationScript,
        keepScript: false);   // tear down after run

    if (!result.IsSuccess)
        throw new InvalidOperationException(
            $"migrate.sh failed (exit {result.ExitCode}): {result.Error}");

    Console.WriteLine(result.Output);
});
```

## The /home vfs root — gotcha

Kudu's vfs maps to `/home` on the App Service host, NOT `/`. So uploading to `wwwroot` is `vfs/site/wwwroot/<file>`, which lands on disk at `/home/site/wwwroot/<file>`. This bit Strata twice during STRATA-356 (seed-data deploy) — the package builds the URL correctly per the convention, but downstream `command` invocations need to reference the absolute `/home/...` path when invoking via `bash`:

```csharp
await kudu.Vfs.UploadTextAsync("site/wwwroot/migrate.sh", scriptBody);
await kudu.Command.ExecuteAsync("bash /home/site/wwwroot/migrate.sh");
```

The `ExecuteBashScriptAsync` convenience method (shown above) bakes both halves of this pattern into a single call.

## Why server-side shell-tokenization is the wrong call

Kudu does NOT shell-tokenize the supplied `command` string. Inline strings work for trivial cases (`ls /home`, `cat /home/site/wwwroot/web.config`) but break on:

- Quoting (`bash -c "echo 'hello world'"` — multiple layers of escaping)
- Pipes / redirects (`some-cli | jq .foo > /tmp/out`)
- Multi-line scripts

The recommended pattern is **always** upload a `.sh` file via vfs, then invoke it. `ExecuteBashScriptAsync` handles both steps in one call and tears down the file unless `keepScript: true`.

## KV-reference diagnostics

When an app setting is configured as a Key Vault reference (`@Microsoft.KeyVault(SecretUri=...)`) and the app is reading the literal placeholder instead of the resolved value, the Management API's `configreferences/appsettings` endpoint reports per-setting resolution status:

```csharp
using var mgmt = new ManagementClient(subId, rg, "strata-api-dev",
    ApiCredential.Bearer(armToken));

var refs = await mgmt.GetConfigReferencesAsync();
foreach (var (key, status) in refs.Properties.KeyToReferenceStatuses)
{
    if (!status.IsResolved)
        Console.WriteLine($"❌ {key}: status={status.Status} details={status.Details}");
}
```

Strata used this exact pattern in STRATA-453 to diagnose a KV-ref resolution failure where the managed identity didn't have `get` permission on the secret. The response payload includes vault name, secret name, identity type, and a `details` field that's usually enough to root-cause the issue.

## Management-side appsettings vs Kudu-side

The two flavors of appsettings have different capabilities:

| Verb | Resolves KV refs? | Auth | When to use |
|---|---|---|---|
| `KuduClient.Settings.SetAsync(...)` | ❌ no | Basic | Plain config values; quick flips from build scripts |
| `ManagementClient.SetAppSettingsAsync(...)` | ✅ yes | Bearer | KV-reference values; the canonical path |

Set a KV reference value via the Management API; quick scalar flips can use either. The KV-resolution path is the one most build scripts want.

## Auth — publishing-profile vs ARM bearer

**Kudu Basic auth** (for `KuduClient`):
- Username from `$publishProfile.userName` (typically starts with `$`, e.g. `$strata-api-dev`)
- Password from `$publishProfile.userPWD`
- Both available via `ManagementClient.ListPublishingCredentialsAsync()` after authenticating with ARM
- Lives at `https://<site>.scm.azurewebsites.net/` — the SCM hostname

**ARM bearer** (for `ManagementClient`):
- Audience: `https://management.azure.com`
- Acquire via `az account get-access-token --resource https://management.azure.com --query accessToken -o tsv`
- Or use `Tamp.AzureCli.V2`'s login + token helpers

The two can chain: ARM bearer → `ListPublishingCredentialsAsync` → Kudu Basic. Strata's pipelines use the chained pattern so they only need to manage one set of credentials (the ARM-side workload identity).

## Sibling packages

- [`Tamp.AzureCli.V2`](https://github.com/tamp-build/tamp-azure-cli) — `az` CLI wrapper; the canonical way to acquire ARM tokens for the Bearer auth path.
- [`Tamp.AzureAppService`](https://github.com/tamp-build/tamp-azure-app-service) — slot-swap + lifecycle via `az webapp` for adopters who'd rather use the CLI for those verbs than the Management API directly.
- [`Tamp.Http`](https://github.com/tamp-build/tamp-http) — the foundation HTTP client this package extends.

## Releasing

Releases follow the [Tamp dogfood pattern](MAINTAINERS.md): bump `<Version>` in `Directory.Build.props`, tag `v<X.Y.Z>`, GitHub Actions runs `dotnet tamp Ci` then `dotnet tamp Push`.

## License

MIT. See [LICENSE](LICENSE).
