using System;
using System.Net.Http;
using Tamp.Http;

namespace Tamp.Kudu;

/// <summary>
/// Typed client for the Azure App Service Kudu REST API. Construct once per site
/// with credentials (publishing-profile Basic auth OR an ARM bearer token), then
/// navigate the endpoint groups via the public properties.
/// </summary>
/// <remarks>
/// <para>
/// Kudu lives at <c>https://&lt;site&gt;.scm.azurewebsites.net</c> for the main app and
/// <c>https://&lt;site&gt;-&lt;slot&gt;.scm.azurewebsites.net</c> for a slot. The SCM hostname
/// is what Kudu binds to; the regular <c>azurewebsites.net</c> host serves the app.
/// </para>
/// <para>
/// Authentication accepts either publishing-profile credentials (HTTP Basic, username
/// from <c>$.publishProfile.userName</c> + password from <c>$.publishProfile.userPWD</c>)
/// or an ARM bearer token (acquired via <c>az account get-access-token --resource
/// https://management.azure.com</c>).
/// </para>
/// <example>
/// <code>
/// var cred = ApiCredential.Basic(userName, new Secret("kudu-password", userPwd));
/// using var kudu = new KuduClient("strata-api-dev", cred);
/// await kudu.Vfs.UploadAsync("site/wwwroot/migrate.sh", File.ReadAllBytes(localPath));
/// var result = await kudu.Command.ExecuteAsync("bash /home/site/wwwroot/migrate.sh", dir: "/home/site/wwwroot");
/// </code>
/// </example>
/// </remarks>
public sealed class KuduClient : TampApiClient
{
    /// <summary>Site hostname (without the <c>.scm.azurewebsites.net</c> suffix).</summary>
    public string SiteName { get; }

    /// <summary>Optional slot name. When set, the client targets the slot's Kudu instance.</summary>
    public string? SlotName { get; }

    public KuduClient(string siteName, ApiCredential credential, string? slot = null, bool disableConnectionVerification = false, HttpClient? http = null)
        : base(BuildBaseUri(siteName, slot), credential, disableConnectionVerification, http, userAgent: "Tamp.Kudu/0.2.0")
    {
        SiteName = siteName ?? throw new ArgumentNullException(nameof(siteName));
        SlotName = slot;
        Vfs = new VfsClient(this);
        Command = new CommandClient(this);
        Settings = new SettingsClient(this);
        Deployment = new DeploymentClient(this);
    }

    /// <summary>vfs operations — upload / download / list / delete files under <c>/home</c>.</summary>
    public VfsClient Vfs { get; }

    /// <summary>Shell command execution under the Kudu console.</summary>
    public CommandClient Command { get; }

    /// <summary>Kudu-flavored app settings. For KV-reference resolution use the Management API instead.</summary>
    public SettingsClient Settings { get; }

    /// <summary>Zip-deploy + deployment introspection (<c>/api/zipdeploy</c>, <c>/api/deployments</c>).</summary>
    public DeploymentClient Deployment { get; }

    private static Uri BuildBaseUri(string siteName, string? slot)
    {
        if (string.IsNullOrWhiteSpace(siteName))
            throw new ArgumentException("siteName must not be null or empty.", nameof(siteName));
        var host = slot is { Length: > 0 } ? $"{siteName}-{slot}" : siteName;
        return new Uri($"https://{host}.scm.azurewebsites.net/");
    }

    // Internal access to the protected HTTP helpers — sub-clients use these to keep
    // each verb's URL construction localized.
    internal Task<T> GetJsonAsync<T>(string relative, CancellationToken ct) => GetAsync<T>(relative, ct);
    internal Task<T> PostJsonAsync_<T>(string relative, object body, CancellationToken ct) => PostJsonAsync<T>(relative, body, ct);
    internal Task PostJsonAsync_(string relative, object body, CancellationToken ct) => PostJsonAsync(relative, body, ct);
    internal Task PutJsonAsync_(string relative, object body, CancellationToken ct) => PutJsonAsync(relative, body, ct);
    internal Task DeleteAsync_(string relative, CancellationToken ct) => DeleteAsync(relative, ct);
    internal Task<HttpResponseMessage> SendRawAsync_(HttpRequestMessage req, HttpCompletionOption opt, CancellationToken ct) => SendRawAsync(req, opt, ct);
}
