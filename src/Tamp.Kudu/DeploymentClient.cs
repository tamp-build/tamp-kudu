using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Tamp.Kudu;

/// <summary>
/// Azure App Service zip-deploy operations via the Kudu <c>/api/zipdeploy</c> endpoint plus the
/// related <c>/api/deployments</c> introspection routes. The canonical deploy path: stream a
/// .zip to the SCM site, poll the deployment ID for completion, return the final status.
/// </summary>
/// <remarks>
/// <para>
/// The endpoint accepts the zip as the raw request body (no multipart wrapping). Authentication
/// is the same Basic-auth publishing-credential model as <see cref="VfsClient"/>; the SCM site
/// handles routing the body into App Service's deployment pipeline.
/// </para>
/// <para>
/// Two completion modes:
/// <list type="bullet">
///   <item><b>Synchronous (default)</b> — caller blocks until provisioning completes. The wrapper
///         posts to <c>/api/zipdeploy</c> without <c>?isAsync=true</c>; Kudu holds the connection
///         open until done. Practical for small zips (&lt;30 MB) and fast provisioning.</item>
///   <item><b>Async + poll</b> — caller passes <c>async: true</c>. The wrapper posts to
///         <c>/api/zipdeploy?isAsync=true</c>, Kudu returns immediately with the deployment ID
///         in the <c>Location</c> header, the wrapper polls <c>/api/deployments/{id}</c> until
///         the deployment reaches a terminal state or <paramref name="timeout"/> elapses.</item>
/// </list>
/// </para>
/// <para>
/// <b>Zip creation is project-side.</b> Use <see cref="System.IO.Compression.ZipFile.CreateFromDirectory"/>
/// or any other zip producer — Tamp does not opine on the layout.
/// </para>
/// </remarks>
public sealed class DeploymentClient
{
    private readonly KuduClient _root;

    internal DeploymentClient(KuduClient root) => _root = root;

    /// <summary>
    /// Zip-deploy from a file path. Wraps a <see cref="FileStream"/> over the path and forwards
    /// to <see cref="ZipDeployAsync(System.IO.Stream, bool, System.TimeSpan?, System.TimeSpan?, System.Threading.CancellationToken)"/>.
    /// </summary>
    /// <param name="zipPath">Absolute or relative path to the zip file.</param>
    /// <param name="async">When true, post with <c>?isAsync=true</c> and poll for completion.
    /// When false (default), Kudu holds the connection open synchronously.</param>
    /// <param name="pollInterval">Interval between polls when <paramref name="async"/> is true.
    /// Default 5 s.</param>
    /// <param name="timeout">Total wait budget. Default 10 min. Applies to the synchronous case
    /// too — the underlying HttpClient gets this as its request timeout.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    public async Task<ZipDeployResult> ZipDeployAsync(
        string zipPath,
        bool async = false,
        TimeSpan? pollInterval = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(zipPath)) throw new ArgumentException("zipPath must not be empty.", nameof(zipPath));
        if (!File.Exists(zipPath)) throw new FileNotFoundException("Zip file not found.", zipPath);
        await using var stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await ZipDeployAsync(stream, async, pollInterval, timeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Zip-deploy from a <see cref="Stream"/>. Useful when the zip is constructed in-memory
    /// or piped from another stage of the build.
    /// </summary>
    public async Task<ZipDeployResult> ZipDeployAsync(
        Stream zipStream,
        bool async = false,
        TimeSpan? pollInterval = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (zipStream is null) throw new ArgumentNullException(nameof(zipStream));

        var resolvedTimeout = timeout ?? TimeSpan.FromMinutes(10);
        var resolvedPollInterval = pollInterval ?? TimeSpan.FromSeconds(5);
        var url = async
            ? new Uri(_root.BaseUri, "api/zipdeploy?isAsync=true")
            : new Uri(_root.BaseUri, "api/zipdeploy");

        using var content = new StreamContent(zipStream);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };

        using var deadline = new CancellationTokenSource(resolvedTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);

        using var response = await _root.SendRawAsync_(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);

        if (!async)
        {
            // Synchronous path: Kudu held the connection until provisioning finished. Response
            // body (when present) is the deployment record.
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
                return new ZipDeployResult(Success:false, DeploymentId:null, Status: "Failed",
                    StatusCode:(int)response.StatusCode, LogText:body);
            }
            return new ZipDeployResult(Success:true, DeploymentId:null, Status: "Success",
                StatusCode:(int)response.StatusCode, LogText:null);
        }

        // Async path: pull the deployment ID from the Location header and poll.
        if (response.StatusCode != HttpStatusCode.Accepted && !response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
            return new ZipDeployResult(Success:false, DeploymentId:null, Status: "Failed",
                StatusCode:(int)response.StatusCode, LogText:body);
        }

        var deploymentId = ExtractDeploymentIdFromLocation(response.Headers.Location)
            ?? throw new InvalidOperationException(
                "Async zip-deploy returned without a Location header from which to extract the deployment id.");

        // Poll loop.
        while (!linked.IsCancellationRequested)
        {
            await Task.Delay(resolvedPollInterval, linked.Token).ConfigureAwait(false);
            var entry = await GetStatusAsync(deploymentId, linked.Token).ConfigureAwait(false);
            if (entry is null) continue;

            // Kudu deployment status codes (from numerical Status field):
            //   0=Success, 1=Failed, 2=Pending(deploying), 3=Building, 4=Deploying.
            // Terminal states: 0, 1. Everything else: keep polling.
            if (entry.Status == 0)
                return new ZipDeployResult(Success:true, DeploymentId:deploymentId,
                    Status: "Success", StatusCode:(int)response.StatusCode,
                    LogText:entry.LogUrl);
            if (entry.Status == 1)
                return new ZipDeployResult(Success:false, DeploymentId:deploymentId,
                    Status: "Failed", StatusCode:(int)response.StatusCode,
                    LogText:entry.LogUrl);
        }

        // Timed out — return a marker result the caller can branch on.
        return new ZipDeployResult(Success:false, DeploymentId:deploymentId,
            Status: "TimedOut", StatusCode:0,
            LogText:$"Polling exceeded {resolvedTimeout.TotalSeconds:0} s before reaching a terminal state.");
    }

    /// <summary>Get the status of a specific deployment by id. Returns null on 404.</summary>
    public async Task<DeploymentEntry?> GetStatusAsync(string deploymentId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(deploymentId)) throw new ArgumentException("deploymentId required.", nameof(deploymentId));
        var url = new Uri(_root.BaseUri, $"api/deployments/{Uri.EscapeDataString(deploymentId)}");
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _root.SendRawAsync_(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return System.Text.Json.JsonSerializer.Deserialize<DeploymentEntry>(body);
    }

    /// <summary>List recent deployments (most-recent-first per Kudu's default ordering).</summary>
    public async Task<IReadOnlyList<DeploymentEntry>> ListAsync(CancellationToken cancellationToken = default)
    {
        var url = new Uri(_root.BaseUri, "api/deployments");
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _root.SendRawAsync_(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return System.Text.Json.JsonSerializer.Deserialize<List<DeploymentEntry>>(body) ?? new List<DeploymentEntry>();
    }

    internal static string? ExtractDeploymentIdFromLocation(Uri? location)
    {
        // Location header shape: https://<site>.scm.azurewebsites.net/api/deployments/{id}
        if (location is null) return null;
        var path = location.AbsolutePath.TrimEnd('/');
        var lastSlash = path.LastIndexOf('/');
        if (lastSlash < 0 || lastSlash == path.Length - 1) return null;
        return path.Substring(lastSlash + 1);
    }
}

/// <summary>Result of a zip-deploy invocation.</summary>
/// <param name="Success">True when the deployment reached the Success terminal state (sync) or polled to status=0 (async).</param>
/// <param name="DeploymentId">The deployment id when known (always populated on the async path; null on the sync path).</param>
/// <param name="Status">Coarse status label: <c>Success</c>, <c>Failed</c>, <c>TimedOut</c>.</param>
/// <param name="StatusCode">HTTP status code from the initial zip-deploy POST.</param>
/// <param name="LogText">Either the failure response body (when the POST failed outright) or the Kudu log URL (when the deployment ran to a terminal state). Null when not applicable.</param>
public sealed record ZipDeployResult(
    bool Success,
    string? DeploymentId,
    string Status,
    int StatusCode,
    string? LogText)
{
    public bool IsSuccess => Success;
}

/// <summary>A row from <c>/api/deployments</c> or <c>/api/deployments/{id}</c>.</summary>
public sealed record DeploymentEntry
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    /// <summary>Numerical status: 0=Success, 1=Failed, 2=Pending/deploying, 3=Building, 4=Deploying.</summary>
    [JsonPropertyName("status")] public int Status { get; init; }
    [JsonPropertyName("status_text")] public string StatusText { get; init; } = "";
    [JsonPropertyName("author_email")] public string? AuthorEmail { get; init; }
    [JsonPropertyName("author")] public string? Author { get; init; }
    [JsonPropertyName("deployer")] public string? Deployer { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
    [JsonPropertyName("progress")] public string? Progress { get; init; }
    [JsonPropertyName("received_time")] public DateTimeOffset? ReceivedTime { get; init; }
    [JsonPropertyName("start_time")] public DateTimeOffset? StartTime { get; init; }
    [JsonPropertyName("end_time")] public DateTimeOffset? EndTime { get; init; }
    [JsonPropertyName("last_success_end_time")] public DateTimeOffset? LastSuccessEndTime { get; init; }
    [JsonPropertyName("complete")] public bool Complete { get; init; }
    [JsonPropertyName("active")] public bool Active { get; init; }
    [JsonPropertyName("is_temp")] public bool IsTemp { get; init; }
    [JsonPropertyName("is_readonly")] public bool IsReadOnly { get; init; }
    [JsonPropertyName("url")] public string? Url { get; init; }
    [JsonPropertyName("log_url")] public string? LogUrl { get; init; }
    [JsonPropertyName("site_name")] public string? SiteName { get; init; }
}
