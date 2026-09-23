using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Win32.SafeHandles;
using Xunit;
using Xunit.Abstractions;

namespace ORelay.Aspire.Hosting.Tests;

public sealed class AppHostIntegrationTests(ITestOutputHelper output)
{
    [RelayIntegrationFact]
    [Trait("Category", "AspireIntegration")]
    public async Task TwoRealAppHostsCompleteFlowsAndResourceRestartReplacesLostRegistration()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = timeout.Token;
        var server = Environment.GetEnvironmentVariable("ORELAY_TEST_SERVER")!;
        using var relay = new HttpClient { BaseAddress = new Uri(server) };
        using var identity = await relay.GetAsync("/health", ct);
        identity.EnsureSuccessStatusCode();
        await using var firstBuilder = await DistributedApplicationTestingBuilder.CreateAsync<Program>(["--RelayUrl", server], ct);
        await using var secondBuilder = await DistributedApplicationTestingBuilder.CreateAsync<Program>(["--RelayUrl", server], ct);
        await using var first = await firstBuilder.BuildAsync(ct);
        await using var second = await secondBuilder.BuildAsync(ct);
        await Task.WhenAll(first.StartAsync(ct), second.StartAsync(ct));
        await Task.WhenAll(WaitForApiAsync(first, ct), WaitForApiAsync(second, ct));
        using var browserOne = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = first.GetEndpoint("api", "http") };
        using var browserTwo = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = second.GetEndpoint("api", "http") };
        var one = await browserOne.GetFromJsonAsync<SampleSession>("/sample/session", ct);
        var two = await browserTwo.GetFromJsonAsync<SampleSession>("/sample/session", ct);
        Assert.NotEqual(one!.RegistrationId, two!.RegistrationId);
        var notifications = first.Services.GetRequiredService<ResourceNotificationService>();
        var relayHealth = first.Services.GetRequiredService<HealthCheckService>();
        await WaitForAsync(() => Task.FromResult(RelayProperties(notifications).TryGetValue("api.status", out var status) && status == "Active"), ct);
        var visible = RelayProperties(notifications);
        Assert.Equal(new Uri(new Uri(server), "/callback").AbsoluteUri, visible["api.publicCallback"]);
        Assert.EndsWith("/oauth/callback", visible["api.destination"], StringComparison.Ordinal);
        Assert.NotNull(visible["api.leaseExpiry"]);
        Assert.NotNull(visible["api.renewalInterval"]);
        Assert.True(notifications.TryGetCurrentState("relay", out var relayEvent));
        Assert.All(relayEvent!.Snapshot.Properties.Where(property => property.Name.StartsWith("api.", StringComparison.Ordinal)),
            property => Assert.True(property.IsHighlighted));
        Assert.Contains(relayEvent!.Snapshot.Relationships, relation => relation.ResourceName == "api");
        Assert.Equal(HealthStatus.Healthy, (await relayHealth.CheckHealthAsync(ct)).Entries["orelay-relay-registrations"].Status);
        var initialExpiry = DateTimeOffset.Parse(visible["api.leaseExpiry"]!, System.Globalization.CultureInfo.InvariantCulture);
        await WaitForAsync(() => Task.FromResult(DateTimeOffset.Parse(RelayProperties(notifications)["api.leaseExpiry"]!, System.Globalization.CultureInfo.InvariantCulture) > initialExpiry), ct);
        Assert.NotNull(RelayProperties(notifications)["api.lastRenewal"]);
        await Task.WhenAll(CompleteFlowAsync(browserOne, ct), CompleteFlowAsync(browserTwo, ct));

        // Keep the browser's legitimate flow cookie while corrupting only the opaque state suffix.
        var flowCookies = new CookieContainer();
        using var manualFlow = new HttpClient(new HttpClientHandler { CookieContainer = flowCookies, AllowAutoRedirect = false }) { BaseAddress = browserOne.BaseAddress };
        using var flowBrowser = new HttpClient(new HttpClientHandler { CookieContainer = flowCookies }) { BaseAddress = browserOne.BaseAddress };
        using var begin = await manualFlow.GetAsync("/login", ct);
        var providerAuthorization = begin.Headers.Location!;
        var legitimateState = QueryHelpers.ParseQuery(providerAuthorization.Query)["state"].ToString();
        var tamperedCallback = QueryHelpers.AddQueryString(new Uri(new Uri(server), "/callback").AbsoluteUri,
            new Dictionary<string, string?> { ["state"] = one.RegistrationId + ".tampered-opaque-state", ["code"] = "synthetic-unused" });
        Assert.StartsWith(one.RegistrationId + ".", legitimateState, StringComparison.Ordinal);
        using var rejected = await flowBrowser.GetAsync(tamperedCallback, ct);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal("/oauth/callback", rejected.RequestMessage!.RequestUri!.AbsolutePath);
        var rejection = await rejected.Content.ReadFromJsonAsync<Dictionary<string, string>>(ct);
        Assert.Equal("invalid_state", rejection!["error"]);
        using var legitimate = await flowBrowser.GetAsync(providerAuthorization, ct);
        Assert.Equal(HttpStatusCode.OK, legitimate.StatusCode);
        var validated = await legitimate.Content.ReadFromJsonAsync<FlowResult>(ct);
        Assert.True(validated!.StateValidated);
        Assert.True(validated.DirectCodeExchange);
        output.WriteLine("Relay forwarded a valid registration with tampered opaque state; the worktree rejected it with 400 invalid_state. The original cookie-bound flow then completed successfully.");

        if (OperatingSystem.IsWindows())
        {
            using var leaseResponse = await relay.PutAsync($"/registrations/{one.RegistrationId}/lease", null, ct);
            var lease = await leaseResponse.Content.ReadFromJsonAsync<Registration>(ct);
            Assert.InRange(lease!.LeaseSeconds, 1, 20);
            using var apiProcess = Process.GetProcessById(one.ProcessId);
            Assert.Equal(0, NativeProcess.NtSuspendProcess(apiProcess.SafeHandle));
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(lease.LeaseSeconds + 2), ct);
                using var noRedirect = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
                using var stillLive = await noRedirect.GetAsync(new Uri(new Uri(server), $"/callback?state={one.RegistrationId}.synthetic"), ct);
                Assert.Equal(HttpStatusCode.Redirect, stillLive.StatusCode);
                output.WriteLine("Suspended the actual API process beyond one full lease. AppHost renewal kept its registration alive.");
            }
            finally { Assert.Equal(0, NativeProcess.NtResumeProcess(apiProcess.SafeHandle)); }
        }

        // Expire only the first application's registration. The running environment cannot change.
        using var delete = await relay.DeleteAsync($"/registrations/{one.RegistrationId}", ct);
        delete.EnsureSuccessStatusCode();
        await WaitForAsync(async () => (await first.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(ct)).Entries["orelay-api"].Description!.Contains("Restart", StringComparison.Ordinal), ct);
        await WaitForAsync(() => Task.FromResult(RelayProperties(notifications)["api.status"] == "Expired or lost"), ct);
        Assert.Equal(HealthStatus.Degraded, (await relayHealth.CheckHealthAsync(ct)).Entries["orelay-relay-registrations"].Status);
        var stale = await browserOne.GetFromJsonAsync<SampleSession>("/sample/session", ct);
        Assert.Equal(one.RegistrationId, stale!.RegistrationId);
        using var callbackClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        using var oldCallback = await callbackClient.GetAsync(new Uri(new Uri(server), $"/callback?state={one.RegistrationId}.synthetic"), ct);
        Assert.Equal(HttpStatusCode.NotFound, oldCallback.StatusCode);
        await CompleteFlowAsync(browserTwo, ct);

        var commands = first.Services.GetRequiredService<ResourceCommandService>();
        var restart = await commands.ExecuteCommandAsync("api", "restart", ct);
        Assert.True(restart.Success, restart.Message);
        await WaitForAsync(async () =>
        {
            try { return (await browserOne.GetFromJsonAsync<SampleSession>("/sample/session", ct))?.RegistrationId != one.RegistrationId; }
            catch (HttpRequestException) { return false; }
        }, ct);
        await CompleteFlowAsync(browserOne, ct);

        await first.StopAsync(ct);
        await WaitForAsync(() => Task.FromResult(RelayProperties(notifications)["api.status"] == "Stopped"), ct);
        using var stopped = await relay.PutAsync($"/registrations/{(await browserTwo.GetFromJsonAsync<SampleSession>("/sample/session", ct))!.RegistrationId}/lease", null, ct);
        Assert.Equal(HttpStatusCode.OK, stopped.StatusCode);
        await CompleteFlowAsync(browserTwo, ct);
        await second.StopAsync(ct);
        using var cleaned = await relay.PutAsync($"/registrations/{two.RegistrationId}/lease", null, ct);
        Assert.Equal(HttpStatusCode.NotFound, cleaned.StatusCode);
    }

    [RelayProcessFact]
    [Trait("Category", "AspireIntegration")]
    public async Task RealRelayProcessRestartRejectsPendingFlowUntilExplicitResourceRestart()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = timeout.Token;
        var binary = Path.GetFullPath(Environment.GetEnvironmentVariable("ORELAY_TEST_RELAY_BINARY")!);
        var runDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../work/verification", "aspire-restart-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(runDirectory);
        using var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        var server = $"http://127.0.0.1:{port}";
        using var management = new HttpClient { BaseAddress = new Uri(server) };
        Process? relayProcess = null;
        Process Launch()
        {
            var start = new ProcessStartInfo(binary.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? "dotnet" : binary)
            { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = runDirectory };
            if (binary.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(binary);
            foreach (var arg in new[] { "server", "--config-file", Path.Combine(runDirectory, "orelay.json"), "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--lease-seconds", "6" }) start.ArgumentList.Add(arg);
            return Process.Start(start) ?? throw new InvalidOperationException("Unable to start owned relay process.");
        }
        async Task Ready()
        {
            await WaitForAsync(async () =>
            {
                Assert.False(relayProcess!.HasExited, "Owned relay exited before becoming ready.");
                try
                {
                    using var response = await management.GetAsync("/health", ct);
                    return response.IsSuccessStatusCode;
                }
                catch (HttpRequestException) { return false; }
            }, ct);
        }
        try
        {
            relayProcess = Launch();
            await Ready();
            await using var builder = await DistributedApplicationTestingBuilder.CreateAsync<Program>(["--RelayUrl", server], ct);
            await using var app = await builder.BuildAsync(ct);
            await app.StartAsync(ct);
            await WaitForApiAsync(app, ct);
            var cookies = new CookieContainer();
            using var browser = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = app.GetEndpoint("api", "http") };
            using var manualBrowser = new HttpClient(new HttpClientHandler { CookieContainer = cookies, AllowAutoRedirect = false }) { BaseAddress = browser.BaseAddress };
            var original = await browser.GetFromJsonAsync<SampleSession>("/sample/session", ct);
            using var login = await manualBrowser.GetAsync("/login", ct);
            using var authorize = await manualBrowser.GetAsync(login.Headers.Location, ct);
            Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
            var pendingCallback = authorize.Headers.Location;
            relayProcess.Kill(entireProcessTree: true);
            await relayProcess.WaitForExitAsync(ct);
            await WaitForAsync(async () => (await app.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(ct)).Entries["orelay-api"].Description!.Contains("Restart", StringComparison.Ordinal), ct);
            relayProcess.Dispose();
            relayProcess = Launch();
            await Ready();
            using var staleCallback = await manualBrowser.GetAsync(pendingCallback, ct);
            Assert.Equal(HttpStatusCode.NotFound, staleCallback.StatusCode);
            Assert.Equal(original!.RegistrationId, (await browser.GetFromJsonAsync<SampleSession>("/sample/session", ct))!.RegistrationId);
            var restarted = await app.Services.GetRequiredService<ResourceCommandService>().ExecuteCommandAsync("api", "restart", ct);
            Assert.True(restarted.Success, restarted.Message);
            await WaitForAsync(async () =>
            {
                try { return (await browser.GetFromJsonAsync<SampleSession>("/sample/session", ct))!.RegistrationId != original.RegistrationId; }
                catch (HttpRequestException) { return false; }
            }, ct);
            await CompleteFlowAsync(browser, ct);
            output.WriteLine("Killed and restarted the owned real CLI relay. Pending callback returned 404; explicit resource restart supplied a fresh ID and completed a new flow.");
            await app.StopAsync(ct);
        }
        finally
        {
            if (relayProcess is not null)
            {
                if (!relayProcess.HasExited) relayProcess.Kill(entireProcessTree: true);
                await relayProcess.WaitForExitAsync(CancellationToken.None);
                relayProcess.Dispose();
            }
        }
    }

    private static async Task WaitForApiAsync(DistributedApplication app, CancellationToken ct)
    {
        await app.ResourceNotifications.WaitForResourceHealthyAsync("api", ct);
        await app.ResourceNotifications.WaitForResourceAsync("provider", KnownResourceStates.Running, ct);
        using var client = new HttpClient { BaseAddress = app.GetEndpoint("api", "http") };
        await WaitForAsync(async () =>
        {
            try { using var response = await client.GetAsync("/health", ct); return response.IsSuccessStatusCode; }
            catch (HttpRequestException) { return false; }
        }, ct);
    }

    private static Dictionary<string, string?> RelayProperties(ResourceNotificationService notifications)
    {
        Assert.True(notifications.TryGetCurrentState("relay", out var relayEvent));
        return relayEvent!.Snapshot.Properties.ToDictionary(property => property.Name, property => property.Value?.ToString(), StringComparer.Ordinal);
    }

    private static async Task CompleteFlowAsync(HttpClient client, CancellationToken ct)
    {
        using var response = await client.GetAsync("/login", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<FlowResult>(ct);
        Assert.True(result!.StateValidated);
        Assert.True(result.DirectCodeExchange);
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition, CancellationToken ct)
    {
        while (!await condition()) await Task.Delay(100, ct);
    }

    private sealed record SampleSession(string RegistrationId, string RedirectUri, DateTimeOffset StartedAt, int ProcessId);
    private sealed record FlowResult(bool StateValidated, bool DirectCodeExchange);
}

internal static partial class NativeProcess
{
    [LibraryImport("ntdll.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int NtSuspendProcess(SafeProcessHandle processHandle);
    [LibraryImport("ntdll.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int NtResumeProcess(SafeProcessHandle processHandle);
}

internal sealed class RelayIntegrationFactAttribute : FactAttribute
{
    public RelayIntegrationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ORELAY_TEST_SERVER")))
            Skip = "Set ORELAY_TEST_SERVER to a run-owned relay with a short lease for real AppHost integration.";
    }
}

internal sealed class RelayProcessFactAttribute : FactAttribute
{
    public RelayProcessFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ORELAY_TEST_RELAY_BINARY")))
            Skip = "Set ORELAY_TEST_RELAY_BINARY to a built real CLI executable or DLL for owned-process restart verification.";
    }
}
