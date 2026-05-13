using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Tamp.Kudu;

/// <summary>
/// Kudu-flavored app settings (<c>/api/settings</c>). These are visible to the running
/// app and persist across deployments, BUT do NOT support Azure Key Vault reference
/// resolution — for KV-refs, use the Management API (<c>configreferences/appsettings</c>
/// endpoint) via <see cref="ManagementClient"/>.
/// </summary>
/// <remarks>
/// Use this for non-secret settings adopters want to flip from a build script (e.g.
/// feature flags, log levels) when the convenience of a Kudu auth (publishing profile)
/// is acceptable. For anything that touches KV, drop to <see cref="ManagementClient"/>.
/// </remarks>
public sealed class SettingsClient
{
    private readonly KuduClient _root;
    internal SettingsClient(KuduClient root) => _root = root;

    /// <summary>Get all settings as a dict.</summary>
    public Task<Dictionary<string, string>> GetAllAsync(CancellationToken ct = default)
        => _root.GetJsonAsync<Dictionary<string, string>>("api/settings", ct);

    /// <summary>Get a single setting by key. Returns null when the key isn't set.</summary>
    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        var all = await GetAllAsync(ct).ConfigureAwait(false);
        return all.TryGetValue(key, out var value) ? value : null;
    }

    /// <summary>
    /// Replace all settings with the supplied dict (Kudu's <c>/api/settings</c> POST is a full
    /// upsert). Returns the resulting state. NOT safe for incremental changes unless the
    /// caller round-trips the full set.
    /// </summary>
    public Task SetAsync(IDictionary<string, string> settings, CancellationToken ct = default)
        => _root.PostJsonAsync_("api/settings", settings, ct);

    /// <summary>Delete a single setting.</summary>
    public Task DeleteAsync(string key, CancellationToken ct = default)
        => _root.DeleteAsync_($"api/settings/{System.Uri.EscapeDataString(key)}", ct);
}
