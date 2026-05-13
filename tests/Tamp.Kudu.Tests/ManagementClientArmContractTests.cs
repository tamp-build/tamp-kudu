using System;
using System.Collections.Generic;
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

/// <summary>
/// TAM-186 — ARM URL contract test fixture. Each public method on <see cref="ManagementClient"/>
/// gets a pinned-canonical URL entry; the test drives the method and asserts the emitted HTTP
/// method, absolute path, and query string match the pin.
///
/// <para>
/// Why this fixture exists: the 0.2.0 → 0.2.1 fix corrected a wrong URL literal
/// (<c>/configreferences/appsettings</c> → <c>/config/configreferences/appsettings</c>) that
/// the ApiClient infrastructure had no way to catch. The bug was caught only when strata-scott
/// adopted the package live. A canonical URL list per endpoint catches the same class of typo
/// at PR time — adding a method without adding its row here fails the table-completeness test;
/// changing a method's URL silently fails its pinned-contract test.
/// </para>
///
/// <para>
/// <b>How to add a new ManagementClient endpoint:</b>
/// <list type="number">
///   <item>Implement the method on <see cref="ManagementClient"/>.</item>
///   <item>Add a row to <see cref="ExpectedContracts"/> with the verb name, HTTP method, expected absolute path, and expected query.</item>
///   <item>Add a switch case to <see cref="DriveVerbAsync"/> that calls the method on the fixture's client.</item>
/// </list>
/// The <see cref="Every_Public_Endpoint_Has_A_Pinned_Contract_Row"/> test guards against the omission case.
/// </para>
/// </summary>
public sealed class ManagementClientArmContractTests
{
    private const string Sub = "sub-uuid-aaaa";
    private const string Rg = "rg-strata-dev";
    private const string Site = "strata-api-dev";
    private const string ApiVersion = "2024-04-01";

    private static string BasePath =>
        $"/subscriptions/{Sub}/resourceGroups/{Rg}/providers/Microsoft.Web/sites/{Site}";

    // ─── Pinned-canonical contract list ───────────────────────────────────
    // Format: (verb, expectedHttpMethod, expectedAbsolutePath, expectedQuery)
    public static IEnumerable<object[]> ExpectedContracts()
    {
        var ver = $"?api-version={ApiVersion}";
        return new[]
        {
            new object[] { "Stop",                     HttpMethod.Post, $"{BasePath}/stop",                                                          ver },
            new object[] { "Start",                    HttpMethod.Post, $"{BasePath}/start",                                                         ver },
            new object[] { "Restart",                  HttpMethod.Post, $"{BasePath}/restart",                                                       ver },
            new object[] { "ListPublishingCredentials", HttpMethod.Post, $"{BasePath}/config/publishingcredentials/list",                            ver },
            new object[] { "GetConfigReferences",      HttpMethod.Get,  $"{BasePath}/config/configreferences/appsettings",                            ver },
            new object[] { "GetConfigReference",       HttpMethod.Get,  $"{BasePath}/config/configreferences/appsettings/ConnectionStrings__Tenant", ver },
            new object[] { "ListAppSettings",          HttpMethod.Post, $"{BasePath}/config/appsettings/list",                                       ver },
            new object[] { "SetAppSettings",           HttpMethod.Put,  $"{BasePath}/config/appsettings",                                            ver },
        };
    }

    [Theory]
    [MemberData(nameof(ExpectedContracts))]
    public async Task ManagementClient_Endpoint_Matches_Pinned_Contract(
        string verb, HttpMethod expectedMethod, string expectedPath, string expectedQuery)
    {
        // ResponseBody covers the GET/POST-with-result calls; verbs that return Task (Stop/Start/Restart/SetAppSettings)
        // ignore it. The empty array shape matches the configreferences raw wire response.
        var spy = new RecordingSpy { ResponseBody = """{"value":[]}""" };
        using var m = new ManagementClient(Sub, Rg, Site,
            ApiCredential.Bearer(new Secret("t", "tok")),
            apiVersion: ApiVersion,
            http: new HttpClient(spy));

        await DriveVerbAsync(m, verb);

        var req = Assert.Single(spy.Requests);
        Assert.Equal(expectedMethod, req.Method);
        Assert.Equal(expectedPath, req.RequestUri!.AbsolutePath);
        Assert.Equal(expectedQuery, req.RequestUri.Query);
    }

    /// <summary>
    /// Defends against "added a new public ManagementClient method but forgot to add its row to
    /// <see cref="ExpectedContracts"/>" — counts the public async-returning surface methods on
    /// <see cref="ManagementClient"/> and asserts it equals the contract-list size.
    /// </summary>
    [Fact]
    public void Every_Public_Endpoint_Has_A_Pinned_Contract_Row()
    {
        // Match every public instance method on ManagementClient that returns Task or Task<T>
        // — these are the wire-touching endpoints we care about. Property getters and helpers
        // declared on TampApiClient (the base) are excluded by DeclaredOnly.
        var endpoints = typeof(ManagementClient)
            .GetMethods(System.Reflection.BindingFlags.Public
                      | System.Reflection.BindingFlags.Instance
                      | System.Reflection.BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)  // exclude property getter/setter
            .Where(m =>
            {
                var rt = m.ReturnType;
                return rt == typeof(Task)
                    || (rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(Task<>));
            })
            .Select(m => m.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        var contracted = ExpectedContracts()
            .Select(row => row[0] + "Async")   // verb in the table is the API-shape name without "Async"
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(endpoints, contracted);
    }

    // ─── verb dispatch ────────────────────────────────────────────────────

    private static async Task DriveVerbAsync(ManagementClient m, string verb)
    {
        switch (verb)
        {
            case "Stop":                      await m.StopAsync(); return;
            case "Start":                     await m.StartAsync(); return;
            case "Restart":                   await m.RestartAsync(); return;
            case "ListPublishingCredentials": await m.ListPublishingCredentialsAsync(); return;
            case "GetConfigReferences":       await m.GetConfigReferencesAsync(); return;
            case "GetConfigReference":        await m.GetConfigReferenceAsync("ConnectionStrings__Tenant"); return;
            case "ListAppSettings":           await m.ListAppSettingsAsync(); return;
            case "SetAppSettings":            await m.SetAppSettingsAsync(new Dictionary<string, string> { ["K"] = "V" }); return;
            default: throw new InvalidOperationException(
                $"Unhandled verb '{verb}' — add a case to {nameof(DriveVerbAsync)} when you add a row to {nameof(ExpectedContracts)}.");
        }
    }

    private sealed class RecordingSpy : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public string ResponseBody { get; set; } = "{}";
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var resp = new HttpResponseMessage(Status)
            {
                Content = new StringContent(ResponseBody, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(resp);
        }
    }
}
