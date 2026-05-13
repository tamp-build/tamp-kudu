using System;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Tamp.Kudu;

/// <summary>
/// Shell command execution under the Kudu console at <c>/api/command</c>.
/// Returns stdout / stderr / exit code in a single response.
/// </summary>
/// <remarks>
/// <para>
/// Kudu does NOT shell-tokenize the supplied command on the server side. For
/// non-trivial scripts, the recommended pattern is:
/// </para>
/// <list type="number">
///   <item>Write a <c>.sh</c> file to <c>/home/site/wwwroot/&lt;name&gt;.sh</c> via <see cref="VfsClient.UploadAsync"/>.</item>
///   <item>Invoke it via <c>ExecuteAsync("bash /home/site/wwwroot/&lt;name&gt;.sh", dir: "/home/site/wwwroot")</c>.</item>
/// </list>
/// <para>
/// Inline shell strings work for short commands (single binary + flags) but break
/// quickly on quoting, pipes, redirects, etc. The wrap-in-a-file pattern sidesteps
/// every quirk.
/// </para>
/// </remarks>
public sealed class CommandClient
{
    private readonly KuduClient _root;
    internal CommandClient(KuduClient root) => _root = root;

    /// <summary>Execute a shell command. Returns stdout / stderr / exit code.</summary>
    public Task<CommandResult> ExecuteAsync(string command, string? dir = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(command)) throw new ArgumentException("Command must not be empty.", nameof(command));
        return _root.PostJsonAsync_<CommandResult>("api/command", new CommandRequest(command, dir ?? "/"), ct);
    }

    /// <summary>
    /// Upload a bash script via vfs and execute it immediately via <c>/api/command</c>.
    /// The recommended pattern for non-trivial scripts — caller passes the script body,
    /// the helper handles the upload + invocation in one call.
    /// </summary>
    /// <param name="scriptName">Filename (e.g. <c>migrate.sh</c>). Will live at <c>site/wwwroot/&lt;scriptName&gt;</c>.</param>
    /// <param name="scriptBody">Script content. Should start with a shebang (<c>#!/usr/bin/env bash</c>).</param>
    /// <param name="workingDir">Working directory for the script's invocation. Default <c>/home/site/wwwroot</c>.</param>
    /// <param name="keepScript">When false (default), the script is deleted after execution. Set true to leave it on the host for diagnostics.</param>
    public async Task<CommandResult> ExecuteBashScriptAsync(
        string scriptName,
        string scriptBody,
        string? workingDir = null,
        bool keepScript = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(scriptName)) throw new ArgumentException("scriptName required.", nameof(scriptName));
        if (string.IsNullOrEmpty(scriptBody)) throw new ArgumentException("scriptBody required.", nameof(scriptBody));

        var vfsPath = "site/wwwroot/" + scriptName;
        await _root.Vfs.UploadTextAsync(vfsPath, scriptBody, overwrite: true, ct).ConfigureAwait(false);
        try
        {
            var dir = workingDir ?? "/home/site/wwwroot";
            return await ExecuteAsync($"bash /home/{vfsPath}", dir, ct).ConfigureAwait(false);
        }
        finally
        {
            if (!keepScript)
            {
                try { await _root.Vfs.DeleteAsync(vfsPath, CancellationToken.None).ConfigureAwait(false); }
                catch { /* best-effort */ }
            }
        }
    }
}

/// <summary>POST body for <c>/api/command</c>.</summary>
public sealed record CommandRequest(
    [property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("dir")] string Dir);

/// <summary>Response from <c>/api/command</c>.</summary>
public sealed record CommandResult
{
    [JsonPropertyName("Output")] public string Output { get; init; } = "";
    [JsonPropertyName("Error")] public string Error { get; init; } = "";
    [JsonPropertyName("ExitCode")] public int ExitCode { get; init; }
    public bool IsSuccess => ExitCode == 0;
}
