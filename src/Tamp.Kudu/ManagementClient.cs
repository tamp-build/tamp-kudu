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
    public Task<ConfigReferencesResponse> GetConfigReferencesAsync(CancellationToken ct = default)
        => GetAsync<ConfigReferencesResponse>($"{BasePath}/config/configreferences/appsettings?api-version={ApiVersion}", ct);

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

/// <summary>Response shape of <c>GET /sites/{}/config/configreferences/appsettings</c>. The single
/// most useful endpoint for diagnosing KV-reference resolution failures.</summary>
public sealed record ConfigReferencesResponse
{
    [JsonPropertyName("properties")] public ConfigReferencesProperties Properties { get; init; } = new();
}

public sealed record ConfigReferencesProperties
{
    [JsonPropertyName("keyToReferenceStatuses")] public Dictionary<string, ConfigReferenceStatus> KeyToReferenceStatuses { get; init; } = new();
}

public sealed record ConfigReferenceStatus
{
    [JsonPropertyName("reference")] public string Reference { get; init; } = "";
    [JsonPropertyName("status")] public string Status { get; init; } = "";
    [JsonPropertyName("vaultName")] public string? VaultName { get; init; }
    [JsonPropertyName("secretName")] public string? SecretName { get; init; }
    [JsonPropertyName("secretVersion")] public string? SecretVersion { get; init; }
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
