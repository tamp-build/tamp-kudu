using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Tamp.Kudu;

/// <summary>
/// Kudu vfs operations. The vfs root maps to <c>/home</c> on the App Service host —
/// so a typical wwwroot upload becomes <c>vfs/site/wwwroot/&lt;file&gt;</c>, NOT
/// <c>vfs/var/...</c>. Path encoding is the responsibility of the caller; relative
/// paths are joined directly without re-escaping.
/// </summary>
public sealed class VfsClient
{
    private readonly KuduClient _root;
    internal VfsClient(KuduClient root) => _root = root;

    /// <summary>
    /// Upload a file to <c>/home/&lt;path&gt;</c>. Returns the absolute URL of the upload target.
    /// Note: Kudu requires the <c>If-Match: "*"</c> header to overwrite existing files;
    /// the helper sets this automatically when <paramref name="overwrite"/> is true.
    /// </summary>
    public async Task<Uri> UploadAsync(string vfsPath, byte[] content, bool overwrite = true, CancellationToken ct = default)
    {
        if (content is null) throw new ArgumentNullException(nameof(content));
        var url = BuildVfsUri(vfsPath, directory: false);
        var req = new HttpRequestMessage(HttpMethod.Put, url) { Content = new ByteArrayContent(content) };
        if (overwrite) req.Headers.TryAddWithoutValidation("If-Match", "\"*\"");
        using var resp = await _root.SendRawAsync_(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        return url;
    }

    /// <summary>Upload a UTF-8 text file. Convenience over byte-array variant.</summary>
    public Task<Uri> UploadTextAsync(string vfsPath, string content, bool overwrite = true, CancellationToken ct = default)
        => UploadAsync(vfsPath, System.Text.Encoding.UTF8.GetBytes(content), overwrite, ct);

    /// <summary>Download a file's bytes. Returns null on 404.</summary>
    public async Task<byte[]?> DownloadAsync(string vfsPath, CancellationToken ct = default)
    {
        var url = BuildVfsUri(vfsPath, directory: false);
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await _root.SendRawAsync_(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    /// <summary>List a directory under <c>/home/&lt;dir&gt;/</c>. Returns the children as <see cref="VfsEntry"/>.</summary>
    public async Task<IReadOnlyList<VfsEntry>> ListAsync(string vfsDir, CancellationToken ct = default)
    {
        var url = BuildVfsUri(vfsDir, directory: true);
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await _root.SendRawAsync_(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return System.Text.Json.JsonSerializer.Deserialize<List<VfsEntry>>(body) ?? new List<VfsEntry>();
    }

    /// <summary>Delete a file under <c>/home/&lt;path&gt;</c>. Returns true if deleted, false if 404.</summary>
    public async Task<bool> DeleteAsync(string vfsPath, CancellationToken ct = default)
    {
        var url = BuildVfsUri(vfsPath, directory: false);
        var req = new HttpRequestMessage(HttpMethod.Delete, url);
        req.Headers.TryAddWithoutValidation("If-Match", "\"*\"");
        using var resp = await _root.SendRawAsync_(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return false;
        resp.EnsureSuccessStatusCode();
        return true;
    }

    /// <summary>
    /// Build the absolute vfs URI for a path. Strips leading slashes from <paramref name="vfsPath"/>
    /// so callers can supply either <c>site/wwwroot/foo</c> or <c>/site/wwwroot/foo</c>. Appends a
    /// trailing slash for directory listings (required by Kudu).
    /// </summary>
    internal Uri BuildVfsUri(string vfsPath, bool directory)
    {
        if (vfsPath is null) throw new ArgumentNullException(nameof(vfsPath));
        var trimmed = vfsPath.TrimStart('/');
        var suffix = directory && !trimmed.EndsWith('/') ? "/" : "";
        return new Uri(_root.BaseUri, "api/vfs/" + trimmed + suffix);
    }
}

/// <summary>One row of a vfs directory listing.</summary>
public sealed record VfsEntry
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("size")] public long Size { get; init; }
    [JsonPropertyName("mtime")] public DateTimeOffset Mtime { get; init; }
    [JsonPropertyName("mime")] public string? Mime { get; init; }
    [JsonPropertyName("href")] public string? Href { get; init; }
    [JsonPropertyName("path")] public string? Path { get; init; }
}
