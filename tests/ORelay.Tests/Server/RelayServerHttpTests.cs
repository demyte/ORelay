using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using ORelay.Server;

namespace ORelay.Tests.Server;

public sealed class RelayServerHttpTests
{
    [Fact]
    public async Task RegistrationAndCallback_PreserveRawQueryAndReturnContractHeaders()
    {
        await using var server = await TestRelayServer.StartAsync();
        using var client = server.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/registrations")
        {
            Content = new StringContent(
                "{\"callbackUrl\":\"http://127.0.0.1:4311/oauth/callback\"}",
                Encoding.UTF8,
                "application/json"),
        };
        using var created = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("no-store", created.Headers.CacheControl?.ToString());
        var registration = await JsonSerializer.DeserializeAsync<RegistrationResponse>(
            await created.Content.ReadAsStreamAsync(),
            RelayJsonContext.Default.RegistrationResponse);
        Assert.NotNull(registration);
        Assert.Equal("http://127.0.0.1:4311/oauth/callback", registration!.CallbackUrl);
        Assert.Equal(server.Options.RelayCallbackUrl, registration.RelayCallbackUrl);
        Assert.Equal(server.Options.LeaseSeconds, registration.LeaseSeconds);

        using var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        var healthBody = await JsonSerializer.DeserializeAsync<RelayHealthResponse>(
            await health.Content.ReadAsStreamAsync(),
            RelayJsonContext.Default.RelayHealthResponse);
        Assert.Equal("orelay", healthBody?.Identity);
        Assert.Equal("ok", healthBody?.Status);

        const string rawQuery = "?state=opaque%2Ework%2Estate&code=a%2Fb%2Bc&scope=x&scope=&error_description=hello%20world";
        using var callback = new HttpRequestMessage(HttpMethod.Get, $"/callback{rawQuery}");
        using var response = await client.SendAsync(callback);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // This input uses a synthetic state and should fail before it can
        // disclose any destination or provider values. A real routing ID is
        // checked below.
        Assert.DoesNotContain("a%2Fb", await response.Content.ReadAsStringAsync());

        var routedState = $"{registration.Id}.opaque%2Fwork.state";
        using var routed = await client.GetAsync(
            $"/callback?state={routedState}&code=a%2Fb%2Bc&scope=x&scope=&error_description=hello%20world",
            HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.Found, routed.StatusCode);
        Assert.Equal(
            "http://127.0.0.1:4311/oauth/callback?state=" + routedState + "&code=a%2Fb%2Bc&scope=x&scope=&error_description=hello%20world",
            routed.Headers.GetValues("Location").Single());
        Assert.Equal("no-store", routed.Headers.CacheControl?.ToString());
        Assert.Equal("no-referrer", routed.Headers.GetValues("Referrer-Policy").Single());
    }

    [Fact]
    public async Task UnknownMalformedAndExpiredRoutes_ReturnSafeErrors()
    {
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-09-22T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        await using var server = await TestRelayServer.StartAsync(clock, leaseSeconds: 1);
        using var client = server.CreateClient();

        using var malformed = await client.GetAsync("/callback?state=missing-dot");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);

        using var unknown = await client.GetAsync("/callback?state=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA.opaque");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var registration = await TestRelayServer.RegisterAsync(client, "http://127.0.0.1:4312/callback");
        clock.Advance(TimeSpan.FromSeconds(1));
        using var expired = await client.GetAsync($"/callback?state={registration.Id}.opaque");
        Assert.Equal(HttpStatusCode.NotFound, expired.StatusCode);
        Assert.DoesNotContain(registration.Id, await expired.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task DeleteIsIdempotent_AndRenewalReturnsNotFoundAfterExpiry()
    {
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-09-22T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        await using var server = await TestRelayServer.StartAsync(clock, leaseSeconds: 5);
        using var client = server.CreateClient();
        var registration = await TestRelayServer.RegisterAsync(client, "https://localhost:4313/callback");

        using var deleted = await client.DeleteAsync($"/registrations/{registration.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var deletedAgain = await client.DeleteAsync($"/registrations/{registration.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deletedAgain.StatusCode);
        using var renewed = await client.PutAsync($"/registrations/{registration.Id}/lease", content: null);
        Assert.Equal(HttpStatusCode.NotFound, renewed.StatusCode);
    }

    private sealed class TestRelayServer : IAsyncDisposable
    {
        private readonly WebApplication app;

        private TestRelayServer(WebApplication app, RelayServerOptions options)
        {
            this.app = app;
            Options = options;
        }

        public RelayServerOptions Options { get; }

        public HttpClient CreateClient() => new(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(app.Urls.Single()),
        };

        public static async Task<TestRelayServer> StartAsync(TestTimeProvider? clock = null, int leaseSeconds = 30)
        {
            var options = new RelayServerOptions { LeaseSeconds = leaseSeconds };
            options.Validate();
            var builder = WebApplication.CreateSlimBuilder(Array.Empty<string>());
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddRelayServer(options, clock ?? new TestTimeProvider(DateTimeOffset.UtcNow));
            var app = builder.Build();
            app.MapRelayEndpoints(app.Services.GetRequiredService<RegistrationStore>(), options);
            await app.StartAsync();
            return new TestRelayServer(app, options);
        }

        public static async Task<RegistrationResponse> RegisterAsync(HttpClient client, string callbackUrl)
        {
            using var response = await client.PostAsJsonAsync(
                "/registrations",
                new CreateRegistrationRequest(callbackUrl),
                RelayJsonContext.Default.CreateRegistrationRequest);
            response.EnsureSuccessStatusCode();
            return (await JsonSerializer.DeserializeAsync<RegistrationResponse>(
                await response.Content.ReadAsStreamAsync(),
                RelayJsonContext.Default.RegistrationResponse))!;
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class TestTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private long utcTicks = initial.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref utcTicks), TimeSpan.Zero);

        public void Advance(TimeSpan amount) => Interlocked.Add(ref utcTicks, amount.Ticks);
    }
}
