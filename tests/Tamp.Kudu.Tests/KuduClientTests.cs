using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Tamp;
using Tamp.Http;
using Tamp.Kudu;
using Xunit;

namespace Tamp.Kudu.Tests;

public sealed class KuduClientTests
{
    // ---- BaseUri construction ----

    [Fact]
    public void Main_Site_Base_Uri_Uses_Scm_Host()
    {
        using var k = new KuduClient("strata-api-dev", FakeCred(), http: FakeHttp());
        Assert.Equal(new Uri("https://strata-api-dev.scm.azurewebsites.net/"), k.BaseUri);
        Assert.Equal("strata-api-dev", k.SiteName);
        Assert.Null(k.SlotName);
    }

    [Fact]
    public void Slot_Base_Uri_Includes_Slot_Suffix()
    {
        using var k = new KuduClient("strata-api-dev", FakeCred(), slot: "staging", http: FakeHttp());
        Assert.Equal(new Uri("https://strata-api-dev-staging.scm.azurewebsites.net/"), k.BaseUri);
        Assert.Equal("staging", k.SlotName);
    }

    [Fact]
    public void Empty_SiteName_Throws()
    {
        Assert.Throws<ArgumentException>(() => new KuduClient("", FakeCred(), http: FakeHttp()));
    }

    [Fact]
    public void Null_SiteName_Throws()
    {
        Assert.Throws<ArgumentException>(() => new KuduClient(null!, FakeCred(), http: FakeHttp()));
    }

    // ---- VfsClient.BuildVfsUri ----

    [Fact]
    public void Vfs_Path_Mounts_Under_Slash_Home_Via_Api_Vfs_Prefix()
    {
        using var k = new KuduClient("strata-api-dev", FakeCred(), http: FakeHttp());
        var url = k.Vfs.BuildVfsUri("site/wwwroot/foo.sh", directory: false);
        Assert.Equal(
            "https://strata-api-dev.scm.azurewebsites.net/api/vfs/site/wwwroot/foo.sh",
            url.AbsoluteUri);
    }

    [Fact]
    public void Vfs_Path_Strips_Leading_Slash()
    {
        using var k = new KuduClient("strata-api-dev", FakeCred(), http: FakeHttp());
        var url = k.Vfs.BuildVfsUri("/site/wwwroot/foo.sh", directory: false);
        // Leading slash on the SUPPLIED path must NOT escape /home — Kudu still scopes
        // to vfs root regardless, but the URL shape must be /api/vfs/site/... not //api/vfs/
        Assert.Equal("/api/vfs/site/wwwroot/foo.sh", url.AbsolutePath);
    }

    [Fact]
    public void Vfs_Directory_Listing_Appends_Trailing_Slash()
    {
        using var k = new KuduClient("strata-api-dev", FakeCred(), http: FakeHttp());
        var url = k.Vfs.BuildVfsUri("site/wwwroot", directory: true);
        Assert.EndsWith("/api/vfs/site/wwwroot/", url.AbsolutePath);
    }

    [Fact]
    public void Vfs_Directory_With_Existing_Trailing_Slash_Not_Doubled()
    {
        using var k = new KuduClient("strata-api-dev", FakeCred(), http: FakeHttp());
        var url = k.Vfs.BuildVfsUri("site/wwwroot/", directory: true);
        Assert.EndsWith("/api/vfs/site/wwwroot/", url.AbsolutePath);
        Assert.False(url.AbsolutePath.EndsWith("//"));
    }

    // ---- Vfs upload uses PUT with If-Match: * ----

    [Fact]
    public async Task Vfs_Upload_Sends_Put_With_If_Match_Header()
    {
        var spy = new RecordingHandler();
        using var k = new KuduClient("strata-api-dev", FakeCred(), http: new HttpClient(spy));
        await k.Vfs.UploadAsync("site/wwwroot/x.sh", Encoding.UTF8.GetBytes("echo hi"), overwrite: true);
        var req = Assert.Single(spy.Requests);
        Assert.Equal(HttpMethod.Put, req.Method);
        Assert.Equal("/api/vfs/site/wwwroot/x.sh", req.RequestUri!.AbsolutePath);
        Assert.True(req.Headers.TryGetValues("If-Match", out var v));
        Assert.Equal("\"*\"", v.Single());
    }

    [Fact]
    public async Task Vfs_Upload_Without_Overwrite_Omits_If_Match()
    {
        var spy = new RecordingHandler();
        using var k = new KuduClient("strata-api-dev", FakeCred(), http: new HttpClient(spy));
        await k.Vfs.UploadAsync("site/wwwroot/x.sh", new byte[] { 1, 2, 3 }, overwrite: false);
        var req = Assert.Single(spy.Requests);
        Assert.False(req.Headers.Contains("If-Match"));
    }

    [Fact]
    public async Task Vfs_Delete_Sends_Delete_With_If_Match_Star()
    {
        var spy = new RecordingHandler();
        using var k = new KuduClient("strata-api-dev", FakeCred(), http: new HttpClient(spy));
        await k.Vfs.DeleteAsync("site/wwwroot/x.sh");
        var req = Assert.Single(spy.Requests);
        Assert.Equal(HttpMethod.Delete, req.Method);
        Assert.True(req.Headers.TryGetValues("If-Match", out _));
    }

    [Fact]
    public async Task Vfs_Delete_Returns_False_On_404()
    {
        var spy = new RecordingHandler { Status = HttpStatusCode.NotFound };
        using var k = new KuduClient("strata-api-dev", FakeCred(), http: new HttpClient(spy));
        Assert.False(await k.Vfs.DeleteAsync("site/wwwroot/nonexistent"));
    }

    [Fact]
    public async Task Vfs_Download_Returns_Null_On_404()
    {
        var spy = new RecordingHandler { Status = HttpStatusCode.NotFound };
        using var k = new KuduClient("strata-api-dev", FakeCred(), http: new HttpClient(spy));
        var bytes = await k.Vfs.DownloadAsync("site/wwwroot/missing.sh");
        Assert.Null(bytes);
    }

    // ---- Command shape ----

    [Fact]
    public async Task Command_Sends_Post_To_Api_Command_With_Command_And_Dir()
    {
        var spy = new RecordingHandler { ResponseBody = "{\"Output\":\"hi\",\"Error\":\"\",\"ExitCode\":0}" };
        using var k = new KuduClient("strata-api-dev", FakeCred(), http: new HttpClient(spy));
        var r = await k.Command.ExecuteAsync("bash /home/site/wwwroot/x.sh", "/home/site/wwwroot");
        var req = Assert.Single(spy.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("/api/command", req.RequestUri!.AbsolutePath);
        var body = spy.RequestBodies.Single();
        Assert.Contains("\"command\":\"bash /home/site/wwwroot/x.sh\"", body);
        Assert.Contains("\"dir\":\"/home/site/wwwroot\"", body);
        Assert.Equal(0, r.ExitCode);
        Assert.True(r.IsSuccess);
    }

    [Fact]
    public async Task Command_Default_Dir_Is_Slash()
    {
        var spy = new RecordingHandler { ResponseBody = "{\"Output\":\"\",\"Error\":\"\",\"ExitCode\":0}" };
        using var k = new KuduClient("strata-api-dev", FakeCred(), http: new HttpClient(spy));
        await k.Command.ExecuteAsync("ls");
        Assert.Contains("\"dir\":\"/\"", spy.RequestBodies.Single());
    }

    [Fact]
    public async Task Command_Empty_Command_Throws()
    {
        using var k = new KuduClient("strata-api-dev", FakeCred(), http: FakeHttp());
        await Assert.ThrowsAsync<ArgumentException>(() => k.Command.ExecuteAsync(""));
    }

    [Fact]
    public async Task ExecuteBashScript_Uploads_Then_Runs_Then_Deletes()
    {
        var spy = new RecordingHandler { ResponseBody = "{\"Output\":\"done\",\"Error\":\"\",\"ExitCode\":0}" };
        using var k = new KuduClient("strata-api-dev", FakeCred(), http: new HttpClient(spy));
        var r = await k.Command.ExecuteBashScriptAsync("migrate.sh", "#!/usr/bin/env bash\necho done");

        // 1) PUT upload  2) POST /api/command  3) DELETE cleanup
        Assert.Equal(3, spy.Requests.Count);
        Assert.Equal(HttpMethod.Put, spy.Requests[0].Method);
        Assert.Equal("/api/vfs/site/wwwroot/migrate.sh", spy.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Post, spy.Requests[1].Method);
        Assert.Equal("/api/command", spy.Requests[1].RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Delete, spy.Requests[2].Method);
        Assert.Equal("done", r.Output);
    }

    [Fact]
    public async Task ExecuteBashScript_KeepScript_Skips_Delete()
    {
        var spy = new RecordingHandler { ResponseBody = "{\"Output\":\"\",\"Error\":\"\",\"ExitCode\":0}" };
        using var k = new KuduClient("strata-api-dev", FakeCred(), http: new HttpClient(spy));
        await k.Command.ExecuteBashScriptAsync("debug.sh", "#!/bin/bash\nls -la", keepScript: true);
        Assert.Equal(2, spy.Requests.Count);
        Assert.DoesNotContain(spy.Requests, r => r.Method == HttpMethod.Delete);
    }

    // ---- Auth header injection (Basic) ----

    [Fact]
    public async Task Basic_Auth_Header_Is_Set_On_Every_Request()
    {
        var spy = new RecordingHandler();
        var cred = ApiCredential.Basic("kudu-user", new Secret("kudu-password", "p@ss"));
        using var k = new KuduClient("strata-api-dev", cred, http: new HttpClient(spy));
        await k.Vfs.UploadAsync("site/wwwroot/x.txt", new byte[] { 0 });
        var req = Assert.Single(spy.Requests);
        Assert.NotNull(req.Headers.Authorization);
        Assert.Equal("Basic", req.Headers.Authorization!.Scheme);
        // Confirm it's the b64 of "kudu-user:p@ss"
        var expectedB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("kudu-user:p@ss"));
        Assert.Equal(expectedB64, req.Headers.Authorization.Parameter);
    }

    // ---- Helpers ----

    private static ApiCredential FakeCred() => ApiCredential.Basic("u", new Secret("pw", "p"));
    private static HttpClient FakeHttp() => new(new RecordingHandler());

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string> RequestBodies { get; } = new();
        public string? ResponseBody { get; set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // Capture the body as a string BEFORE disposing the response (which disposes the request).
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            RequestBodies.Add(body);
            Requests.Add(request);
            var resp = new HttpResponseMessage(Status);
            if (ResponseBody is not null)
            {
                resp.Content = new StringContent(ResponseBody, Encoding.UTF8, "application/json");
            }
            return resp;
        }
    }
}

public sealed class ManagementClientTests
{
    [Fact]
    public void Base_Uri_Is_Management_Azure_Com()
    {
        using var m = new ManagementClient("sub", "rg", "site", ApiCredential.Bearer(new Secret("t", "tok")),
            http: new HttpClient(new EchoHandler()));
        Assert.Equal(new Uri("https://management.azure.com/"), m.BaseUri);
    }

    [Fact]
    public async Task Restart_Posts_To_Restart_With_Api_Version()
    {
        var spy = new SpyHandler();
        using var m = new ManagementClient("sub-uuid", "rg-strata", "strata-api",
            ApiCredential.Bearer(new Secret("t", "tok")),
            http: new HttpClient(spy));
        await m.RestartAsync();
        var req = Assert.Single(spy.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Contains("/subscriptions/sub-uuid/resourceGroups/rg-strata/providers/Microsoft.Web/sites/strata-api/restart",
            req.RequestUri!.AbsolutePath);
        Assert.Contains("api-version=2024-04-01", req.RequestUri.Query);
    }

    [Fact]
    public async Task GetConfigReferences_Get_To_Configreferences_Appsettings()
    {
        var spy = new SpyHandler
        {
            ResponseBody = "{\"properties\":{\"keyToReferenceStatuses\":{\"MY_SECRET\":{\"reference\":\"@KV\",\"status\":\"Resolved\",\"vaultName\":\"kv1\"}}}}"
        };
        using var m = new ManagementClient("sub", "rg", "site",
            ApiCredential.Bearer(new Secret("t", "tok")),
            http: new HttpClient(spy));
        var refs = await m.GetConfigReferencesAsync();
        var entry = refs.Properties.KeyToReferenceStatuses["MY_SECRET"];
        Assert.True(entry.IsResolved);
        Assert.Equal("kv1", entry.VaultName);
        var req = Assert.Single(spy.Requests);
        Assert.Equal(HttpMethod.Get, req.Method);
        // ARM serves the resolution-status endpoint at /sites/{}/config/configreferences/appsettings.
        // The full /config/ segment is part of the WebApps configuration family alongside
        // /config/appsettings/list and /config/connectionstrings/list — regression fence for the
        // 0.2.1 URL fix (the 0.1.0/0.2.0 implementations omitted /config/ and 404'd).
        Assert.Contains("/config/configreferences/appsettings", req.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Bearer_Auth_Header_Is_Set()
    {
        var spy = new SpyHandler();
        using var m = new ManagementClient("sub", "rg", "site",
            ApiCredential.Bearer(new Secret("arm-token", "eyJhbGc...")),
            http: new HttpClient(spy));
        await m.StartAsync();
        var req = Assert.Single(spy.Requests);
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
        Assert.Equal("eyJhbGc...", req.Headers.Authorization.Parameter);
    }

    private sealed class SpyHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string> RequestBodies { get; } = new();
        public string ResponseBody { get; set; } = "{}";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            RequestBodies.Add(body);
            Requests.Add(request);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ResponseBody, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class EchoHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
    }
}
