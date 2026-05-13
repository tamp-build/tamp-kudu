using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Tamp;
using Tamp.Http;
using Tamp.Kudu;
using Xunit;

namespace Tamp.Kudu.Tests;

public sealed class DeploymentClientTests
{
    private static ApiCredential FakeCred() => ApiCredential.Basic("u", new Secret("pw", "p"));

    [Fact]
    public async Task ZipDeploy_Stream_Sync_Posts_To_ApiZipDeploy_With_Application_Zip_Body()
    {
        var spy = new RecordingHandler { Status = HttpStatusCode.OK };
        using var k = new KuduClient("strata-api-dev", FakeCred(), http: new HttpClient(spy));
        var zip = new MemoryStream(new byte[] { 0x50, 0x4B, 0x03, 0x04 });   // PK.. zip header

        var result = await k.Deployment.ZipDeployAsync(zip, async: false);

        var req = Assert.Single(spy.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("/api/zipdeploy", req.RequestUri!.AbsolutePath);
        Assert.Empty(req.RequestUri.Query);  // no ?isAsync=true for sync mode
        Assert.Equal("application/zip", req.Content!.Headers.ContentType!.MediaType);
        Assert.True(result.IsSuccess);
        Assert.Equal("Success", result.Status);
    }

    [Fact]
    public async Task ZipDeploy_Sync_HttpError_Returns_Failed_Result()
    {
        var spy = new RecordingHandler { Status = HttpStatusCode.Conflict, ResponseBody = "deployment in progress" };
        using var k = new KuduClient("strata-api-dev", FakeCred(), http: new HttpClient(spy));
        var zip = new MemoryStream(new byte[] { 0 });

        var result = await k.Deployment.ZipDeployAsync(zip, async: false);
        Assert.False(result.IsSuccess);
        Assert.Equal("Failed", result.Status);
        Assert.Equal((int)HttpStatusCode.Conflict, result.StatusCode);
        Assert.Equal("deployment in progress", result.LogText);
    }

    [Fact]
    public async Task ZipDeploy_Async_Path_Includes_IsAsync_Query_And_Polls()
    {
        // First call: POST returns 202 Accepted with Location: /api/deployments/abc123
        // Polling: GET /api/deployments/abc123 returns status=2 (pending) twice then status=0 (success).
        var pollResponses = new Queue<string>(new[]
        {
            """{"id":"abc123","status":2,"status_text":"Pending","complete":false,"log_url":"https://x/log/abc123"}""",
            """{"id":"abc123","status":0,"status_text":"Success","complete":true,"log_url":"https://x/log/abc123"}""",
        });
        var spy = new ScriptedHandler(req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                var resp = new HttpResponseMessage(HttpStatusCode.Accepted);
                resp.Headers.Location = new Uri("https://strata-api-dev.scm.azurewebsites.net/api/deployments/abc123");
                return resp;
            }
            // GET poll
            var body = pollResponses.Dequeue();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        });
        using var k = new KuduClient("strata-api-dev", FakeCred(), http: new HttpClient(spy));
        var zip = new MemoryStream(new byte[] { 0 });

        var result = await k.Deployment.ZipDeployAsync(zip, async: true, pollInterval: TimeSpan.FromMilliseconds(5));

        Assert.True(result.IsSuccess);
        Assert.Equal("abc123", result.DeploymentId);
        Assert.Equal("Success", result.Status);
        // POST + 2 polls = 3 requests total
        Assert.Equal(3, spy.Requests.Count);
        // First request had ?isAsync=true
        Assert.Contains("isAsync=true", spy.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task ZipDeploy_Async_Path_Polls_Until_Failure_State()
    {
        var spy = new ScriptedHandler(req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                var resp = new HttpResponseMessage(HttpStatusCode.Accepted);
                resp.Headers.Location = new Uri("https://strata-api-dev.scm.azurewebsites.net/api/deployments/badid");
                return resp;
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"id":"badid","status":1,"status_text":"Failed","complete":true,"log_url":"https://x/log/badid"}""",
                    Encoding.UTF8, "application/json"),
            };
        });
        using var k = new KuduClient("strata-api-dev", FakeCred(), http: new HttpClient(spy));
        var zip = new MemoryStream(new byte[] { 0 });

        var result = await k.Deployment.ZipDeployAsync(zip, async: true, pollInterval: TimeSpan.FromMilliseconds(5));
        Assert.False(result.IsSuccess);
        Assert.Equal("Failed", result.Status);
        Assert.Equal("badid", result.DeploymentId);
    }

    [Fact]
    public async Task ZipDeploy_Path_Rejects_Missing_File()
    {
        using var k = new KuduClient("strata-api-dev", FakeCred(), http: new HttpClient(new RecordingHandler()));
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            k.Deployment.ZipDeployAsync("/no/such/path.zip"));
    }

    [Fact]
    public async Task ZipDeploy_Stream_Rejects_Null()
    {
        using var k = new KuduClient("strata-api-dev", FakeCred(), http: new HttpClient(new RecordingHandler()));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            k.Deployment.ZipDeployAsync((Stream)null!));
    }

    [Fact]
    public async Task GetStatusAsync_Returns_Null_On_404()
    {
        var spy = new RecordingHandler { Status = HttpStatusCode.NotFound };
        using var k = new KuduClient("strata-api-dev", FakeCred(), http: new HttpClient(spy));
        var entry = await k.Deployment.GetStatusAsync("xyz");
        Assert.Null(entry);
    }

    [Fact]
    public async Task GetStatusAsync_Parses_Successful_Response()
    {
        var spy = new RecordingHandler
        {
            ResponseBody = """{"id":"abc","status":0,"status_text":"Success","complete":true,"site_name":"strata-api-dev"}"""
        };
        using var k = new KuduClient("strata-api-dev", FakeCred(), http: new HttpClient(spy));
        var entry = await k.Deployment.GetStatusAsync("abc");
        Assert.NotNull(entry);
        Assert.Equal("abc", entry!.Id);
        Assert.Equal(0, entry.Status);
        Assert.Equal("Success", entry.StatusText);
        Assert.True(entry.Complete);
    }

    [Fact]
    public async Task ListAsync_Returns_All_Deployments()
    {
        var spy = new RecordingHandler
        {
            ResponseBody = """[{"id":"d1","status":0},{"id":"d2","status":1}]"""
        };
        using var k = new KuduClient("strata-api-dev", FakeCred(), http: new HttpClient(spy));
        var list = await k.Deployment.ListAsync();
        Assert.Equal(2, list.Count);
        Assert.Equal("d1", list[0].Id);
        Assert.Equal(1, list[1].Status);
    }

    [Fact]
    public void ExtractDeploymentIdFromLocation_Pulls_Last_Path_Segment()
    {
        Assert.Equal("abc-123",
            DeploymentClient.ExtractDeploymentIdFromLocation(new Uri("https://x.scm.azurewebsites.net/api/deployments/abc-123")));
        Assert.Equal("xyz",
            DeploymentClient.ExtractDeploymentIdFromLocation(new Uri("https://x/api/deployments/xyz/")));
        Assert.Null(DeploymentClient.ExtractDeploymentIdFromLocation(null));
    }

    // ---- helpers ----

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public string? ResponseBody { get; set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Content is not null) await request.Content.LoadIntoBufferAsync();
            Requests.Add(request);
            var resp = new HttpResponseMessage(Status);
            if (ResponseBody is not null)
            {
                resp.Content = new StringContent(ResponseBody, Encoding.UTF8, "application/json");
            }
            return resp;
        }
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _script;
        public List<HttpRequestMessage> Requests { get; } = new();
        public ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> script) => _script = script;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Content is not null) await request.Content.LoadIntoBufferAsync();
            Requests.Add(request);
            return _script(request);
        }
    }
}
