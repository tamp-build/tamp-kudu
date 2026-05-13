using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Tamp.Http;

namespace Tamp.Kudu;

/// <summary>
/// Azure Resource Manager (ARM) helpers for App Service operations that aren't on the
/// Kudu surface: lifecycle (stop / start / restart), publishing-credential listing
/// (<c>listKeys</c>), and the <c>config/configreferences/appsettings</c> diagnostic that
/// reports per-setting KV-reference resolution status.
/// </summary>
/// <remarks>
/// <para>
/// Auth is an ARM bearer token (audience <c>https://management.azure.com</c>). Acquire
/// via <c>az account get-access-token --resource https://management.azure.com</c> or
/// via Tamp.AzureCli.V2's login flow. Pass as <see cref="ApiCredential.Bearer"/>.
/// </para>
/// <example>
/// <code>
/// var token = AzureCli.GetAccessToken(...);  // ARM resource
/// using var mgmt = new ManagementClient(subscriptionId, resourceGroup, siteName,
///     ApiCredential.Bearer(token));
/// await mgmt.RestartAsync();
/// var refs = await mgmt.GetConfigReferencesAsync();
/// foreach (var r in refs.Properties.KeyToReferenceStatuses.Values)
///     Console.WriteLine($"{r.Reference} → {r.Status}: {r.Details}");
/// </code>
/// </example>
/// </remarks>
public sealed class ManagementClient : TampApiClient
{
    public string SubscriptionId { get; }
    public string ResourceGroup { get; }
    public string SiteName { get; }

    /// <summary>API version. Default <c>2024-04-01</c> — the GA Management API for App Service as of mid-2025.</summary>
    public string ApiVersion { get; }

    public ManagementClient(
        string subscriptionId,
        string resourceGroup,
        string siteName,
        ApiCredential credential,
        string apiVersion = "2024-04-01",
        HttpClient? http = null)
        : base(new Uri("https://management.azure.com/"), credential, disableConnectionVerification: false, http, userAgent: "Tamp.Kudu.Management/0.1.0")
    {
        SubscriptionId = subscriptionId ?? throw new ArgumentNullException(nameof(subscriptionId));
        ResourceGroup = resourceGroup ?? throw new ArgumentNullException(nameof(resourceGroup));
        SiteName = siteName ?? throw new ArgumentNullException(nameof(siteName));
        ApiVersion = apiVersion;
    }

    private string BasePath =>
        $"subscriptions/{Uri.EscapeDataString(SubscriptionId)}" +
        $"/resourceGroups/{Uri.EscapeDataString(ResourceGroup)}" +
        $"/providers/Microsoft.Web/sites/{Uri.EscapeDataString(SiteName)}";

    /// <summary>Stop the site.</summary>
    public Task StopAsync(CancellationToken ct = default) => PostNoBodyAsync($"{BasePath}/stop?api-version={ApiVersion}", ct);

    /// <summary>Start the site.</summary>
    public Task StartAsync(CancellationToken ct = default) => PostNoBodyAsync($"{BasePath}/start?api-version={ApiVersion}", ct);

    /// <summary>Restart the site.</summary>
    public Task RestartAsync(CancellationToken ct = default) => PostNoBodyAsync($"{BasePath}/restart?api-version={ApiVersion}", ct);

    /// <summary>Return publishing credentials (username + scm-uri + password). The credentials are downloaded as a typed object; consumers wrap the password in a <see cref="Secret"/> at the call site.</summary>
    public async Task<PublishingCredentials> ListPublishingCredentialsAsync(CancellationToken ct = default)
    {
        var body = await PostJsonAsync<PublishingCredentials>(
            $"{BasePath}/config/publishingcredentials/list?api-version={ApiVersion}",
            new { }, ct).ConfigureAwait(false);
        return body;
    }

    /// <summary>
    /// Read the per-app-setting KV-reference resolution status. The most useful endpoint for
    /// diagnosing "KV-ref says resolved but the app is reading the literal placeholder" failures.
    /// </summary>
    /// <remarks>
    /// The ARM endpoint returns a resource-list shape — <c>{ value: [{ id, name, properties: {...} }, ...] }</c>.
    /// This method projects it into the dict-by-name shape exposed via <see cref="ConfigReferencesResponse"/>
    /// for ergonomic adopter code (<c>refs.Properties.KeyToReferenceStatuses[settingName]</c>). When the
    /// raw resource list shape is needed (debugging, side-by-side display), call
    /// <see cref="GetConfigReferenceAsync"/> per setting or consume the raw model directly via
    /// <c>GetAsync&lt;<see cref="ConfigReferencesRawResponse"/>&gt;(...)</c>.
    /// </remarks>
    public async Task<ConfigReferencesResponse> GetConfigReferencesAsync(CancellationToken ct = default)
    {
        var raw = await GetAsync<ConfigReferencesRawResponse>(
            $"{BasePath}/config/configreferences/appsettings?api-version={ApiVersion}", ct).ConfigureAwait(false);
        var dict = new Dictionary<string, ConfigReferenceStatus>(raw.Value.Count, StringComparer.Ordinal);
        foreach (var entry in raw.Value)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            dict[entry.Name] = entry.Properties;
        }
        return new ConfigReferencesResponse
        {
            Properties = new ConfigReferencesProperties { KeyToReferenceStatuses = dict }
        };
    }

    /// <summary>
    /// Read the KV-reference status for a single app setting via the per-resource endpoint
    /// <c>/config/configreferences/appsettings/{settingName}</c>. Returns null on 404 (the setting
    /// has no KV reference configured, or the setting doesn't exist).
    /// </summary>
    public async Task<ConfigReferenceStatus?> GetConfigReferenceAsync(string settingName, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(settingName)) throw new ArgumentException("settingName required.", nameof(settingName));
        var url = $"{BasePath}/config/configreferences/appsettings/{Uri.EscapeDataString(settingName)}?api-version={ApiVersion}";
        try
        {
            var entry = await GetAsync<ConfigReferenceResourceEntry>(url, ct).ConfigureAwait(false);
            return entry.Properties;
        }
        catch (ApiClientException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>Read all current app settings via the Management API (the route that resolves KV references).</summary>
    public Task<AppSettingsResponse> ListAppSettingsAsync(CancellationToken ct = default)
        => PostJsonAsync<AppSettingsResponse>($"{BasePath}/config/appsettings/list?api-version={ApiVersion}", new { }, ct);

    /// <summary>
    /// Set all app settings via the Management API. This route DOES resolve KV references, so
    /// setting a value like <c>@Microsoft.KeyVault(SecretUri=...)</c> is the canonical pattern.
    /// Replaces the entire set — caller must round-trip if doing incremental updates.
    /// </summary>
    public Task SetAppSettingsAsync(IDictionary<string, string> settings, CancellationToken ct = default)
    {
        var body = new { properties = settings };
        return PutJsonAsync($"{BasePath}/config/appsettings?api-version={ApiVersion}", body, ct);
    }

    private async Task PostNoBodyAsync(string relative, CancellationToken ct)
    {
        // ARM accepts POST with no body for these verbs; we serialize an empty object
        // to keep the content type correct.
        await PostJsonAsync(relative, new { }, ct).ConfigureAwait(false);
    }
}

/// <summary>Response shape of <c>POST /config/publishingcredentials/list</c>.</summary>
public sealed record PublishingCredentials
{
    [JsonPropertyName("properties")] public PublishingCredentialsProperties Properties { get; init; } = new();
}

public sealed record PublishingCredentialsProperties
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("publishingUserName")] public string PublishingUserName { get; init; } = "";
    [JsonPropertyName("publishingPassword")] public string PublishingPassword { get; init; } = "";
    [JsonPropertyName("scmUri")] public string ScmUri { get; init; } = "";
}

/// <summary>
/// Adopter-facing, dict-shaped projection of the configreferences/appsettings endpoint.
/// The wire shape is a resource list (see <see cref="ConfigReferencesRawResponse"/>);
/// <see cref="ManagementClient.GetConfigReferencesAsync"/> projects it into this shape
/// keyed by setting name for ergonomic <c>refs.Properties.KeyToReferenceStatuses[settingName]</c>
/// lookups.
/// </summary>
public sealed record ConfigReferencesResponse
{
    [JsonPropertyName("properties")] public ConfigReferencesProperties Properties { get; init; } = new();
}

public sealed record ConfigReferencesProperties
{
    [JsonPropertyName("keyToReferenceStatuses")] public Dictionary<string, ConfigReferenceStatus> KeyToReferenceStatuses { get; init; } = new();
}

/// <summary>
/// Raw wire shape returned by <c>GET /config/configreferences/appsettings</c>:
/// <c>{ value: [{ id, name, location, properties: {...}, type }, ...] }</c>. Exposed as
/// public so adopters who want the full resource metadata (id, location, type) can call
/// <see cref="ManagementClient.GetAsync{T}"/> directly with this type, bypassing the
/// dict projection done by <see cref="ManagementClient.GetConfigReferencesAsync"/>.
/// </summary>
public sealed record ConfigReferencesRawResponse
{
    [JsonPropertyName("value")] public List<ConfigReferenceResourceEntry> Value { get; init; } = new();
}

/// <summary>One row of the <see cref="ConfigReferencesRawResponse"/> resource list.</summary>
public sealed record ConfigReferenceResourceEntry
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("location")] public string? Location { get; init; }
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("properties")] public ConfigReferenceStatus Properties { get; init; } = new();
}

public sealed record ConfigReferenceStatus
{
    [JsonPropertyName("reference")] public string Reference { get; init; } = "";
    [JsonPropertyName("status")] public string Status { get; init; } = "";
    [JsonPropertyName("vaultName")] public string? VaultName { get; init; }
    [JsonPropertyName("secretName")] public string? SecretName { get; init; }
    [JsonPropertyName("secretVersion")] public string? SecretVersion { get; init; }
    /// <summary>Currently-resolved version of the secret (the GUID portion of the active KV reference URI).
    /// Useful when a setting points at a versionless KV reference and you want to know which version is in
    /// flight right now (rotation-tracking).</summary>
    [JsonPropertyName("activeVersion")] public string? ActiveVersion { get; init; }
    [JsonPropertyName("identityType")] public string? IdentityType { get; init; }
    [JsonPropertyName("details")] public string? Details { get; init; }
    /// <summary>True when the KV reference resolved to a real value; false otherwise (details explains why).</summary>
    [JsonIgnore] public bool IsResolved => string.Equals(Status, "Resolved", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Response shape of <c>POST /config/appsettings/list</c>.</summary>
public sealed record AppSettingsResponse
{
    [JsonPropertyName("properties")] public Dictionary<string, string> Properties { get; init; } = new();
}
