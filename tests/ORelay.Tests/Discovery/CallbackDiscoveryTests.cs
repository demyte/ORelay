using ORelay.Configuration;
using ORelay.Discovery;

namespace ORelay.Tests.Discovery;

public sealed class CallbackDiscoveryTests
{
    [Fact]
    public async Task ExplicitUrlWinsWithoutAnAspireEndpointOrTailscale()
    {
        var provider = new FakeTailscaleProvider(TailscaleStatusSnapshot.Unavailable("not installed"));
        var discovery = new CallbackAddressDiscovery(provider);

        var result = await discovery.ResolveAsync(new CallbackDiscoveryRequest
        {
            Mode = CallbackDiscoveryMode.Tailscale,
            ExplicitUrl = "https://relay.example.test/worktree/callback",
        });

        Assert.True(result.Succeeded);
        Assert.Equal("https://relay.example.test/worktree/callback", result.CallbackUrl);
        Assert.Equal("explicit-url", result.Source);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task HostnameOverridePreservesEndpointSchemePortAndPath()
    {
        var discovery = new CallbackAddressDiscovery();

        var result = await discovery.ResolveAsync(new CallbackDiscoveryRequest
        {
            Hostname = "worktree.tailnet.test",
            AspireEndpoint = new AspireApplicationEndpoint("https://127.0.0.1:54321/oauth/callback"),
        });

        Assert.True(result.Succeeded);
        Assert.Equal("https://worktree.tailnet.test:54321/oauth/callback", result.CallbackUrl);
    }

    [Fact]
    public async Task HostnameOverrideFormatsIpv6WithoutAmbiguousConcatenation()
    {
        var discovery = new CallbackAddressDiscovery();

        var result = await discovery.ResolveAsync(new CallbackDiscoveryRequest
        {
            Hostname = "2001:db8::42",
            AspireEndpoint = new AspireApplicationEndpoint("http://localhost:54321/oauth/callback"),
        });

        Assert.True(result.Succeeded);
        Assert.Equal("http://[2001:db8::42]:54321/oauth/callback", result.CallbackUrl);
    }

    [Fact]
    public async Task TailscaleUnavailableAndAmbiguousResultsRequireAnExplicitOverride()
    {
        var unavailable = new CallbackAddressDiscovery(new FakeTailscaleProvider(
            TailscaleStatusSnapshot.Unavailable("daemon is stopped")));
        var unavailableResult = await unavailable.ResolveAsync(new CallbackDiscoveryRequest
        {
            Mode = CallbackDiscoveryMode.Tailscale,
            AspireEndpoint = new AspireApplicationEndpoint("http://127.0.0.1:4567/callback"),
        });

        Assert.False(unavailableResult.Succeeded);
        Assert.Equal(nameof(CallbackDiscoveryFailureCode.TailscaleUnavailable), unavailableResult.ErrorCode);
        Assert.Contains("explicit", unavailableResult.NextStep, StringComparison.OrdinalIgnoreCase);

        var ambiguous = new CallbackAddressDiscovery(new FakeTailscaleProvider(
            new TailscaleStatusSnapshot(true, "Running", null, null, ["100.64.0.1", "100.64.0.2"])));
        var ambiguousResult = await ambiguous.ResolveAsync(new CallbackDiscoveryRequest
        {
            Mode = CallbackDiscoveryMode.Tailscale,
            AspireEndpoint = new AspireApplicationEndpoint("http://127.0.0.1:4567/callback"),
        });

        Assert.False(ambiguousResult.Succeeded);
        Assert.Equal(nameof(CallbackDiscoveryFailureCode.TailscaleAmbiguous), ambiguousResult.ErrorCode);
        Assert.Equal(["100.64.0.1", "100.64.0.2"], ambiguousResult.Candidates);
    }

    [Fact]
    public async Task RemoteHostDoesNotHideLoopbackOnlyApplicationBinding()
    {
        var discovery = new CallbackAddressDiscovery(new FakeTailscaleProvider(
            new TailscaleStatusSnapshot(true, "Running", "worktree.tailnet.test", null, [])));

        var result = await discovery.ResolveAsync(new CallbackDiscoveryRequest
        {
            Mode = CallbackDiscoveryMode.Tailscale,
            AspireEndpoint = new AspireApplicationEndpoint("http://127.0.0.1:4567/callback", isLoopbackOnly: true),
        });

        Assert.False(result.Succeeded);
        Assert.Equal(nameof(CallbackDiscoveryFailureCode.IncompatibleBinding), result.ErrorCode);
        Assert.Contains("reachable interface", result.NextStep, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RelaySettingsDiscoveryHonorsExplicitValuesBeforeTailscale()
    {
        var settings = new RelaySettings
        {
            Bind = "0.0.0.0",
            PublicUrl = "https://relay.example.test",
            AutoDiscovery = "tailscale",
        };
        var provider = new FakeTailscaleProvider(TailscaleStatusSnapshot.Unavailable("not installed"));

        var result = await RelaySettingsDiscovery.ResolveAsync(settings, provider);

        Assert.True(result.Succeeded);
        Assert.Equal("explicit-public-url", result.Source);
        Assert.Same(settings, result.Settings);
        Assert.Equal(0, provider.Calls);
    }

    [Theory]
    [InlineData("bad?host")]
    [InlineData("bad#host")]
    [InlineData("[::]")]
    public async Task InvalidHostnameOverrideIsRejected(string hostname)
    {
        var discovery = new CallbackAddressDiscovery();

        var result = await discovery.ResolveAsync(new CallbackDiscoveryRequest
        {
            Hostname = hostname,
            AspireEndpoint = new AspireApplicationEndpoint("http://127.0.0.1:4567/callback"),
        });

        Assert.False(result.Succeeded);
        Assert.Equal(nameof(CallbackDiscoveryFailureCode.InvalidHostname), result.ErrorCode);
    }

    [Fact]
    public async Task PortZeroIsRejectedForExplicitUrlAndAllocatedEndpoint()
    {
        var discovery = new CallbackAddressDiscovery();

        var explicitResult = await discovery.ResolveAsync(new CallbackDiscoveryRequest
        {
            ExplicitUrl = "http://127.0.0.1:0/callback",
        });
        Assert.False(explicitResult.Succeeded);
        Assert.Equal(nameof(CallbackDiscoveryFailureCode.InvalidExplicitUrl), explicitResult.ErrorCode);

        var endpointResult = await discovery.ResolveAsync(new CallbackDiscoveryRequest
        {
            AspireEndpoint = new AspireApplicationEndpoint("http://127.0.0.1:0/callback"),
        });
        Assert.False(endpointResult.Succeeded);
        Assert.Equal(nameof(CallbackDiscoveryFailureCode.InvalidEndpoint), endpointResult.ErrorCode);
    }

    [Fact]
    public async Task InvalidTailscaleHostsAreDiscardedBeforeSelection()
    {
        var discovery = new CallbackAddressDiscovery(new FakeTailscaleProvider(
            new TailscaleStatusSnapshot(true, "Running", "?", "@", ["#", "100.64.0.7"])));

        var result = await discovery.ResolveAsync(new CallbackDiscoveryRequest
        {
            Mode = CallbackDiscoveryMode.Tailscale,
            AspireEndpoint = new AspireApplicationEndpoint("http://127.0.0.1:4567/callback"),
        });

        Assert.True(result.Succeeded);
        Assert.Equal("http://100.64.0.7:4567/callback", result.CallbackUrl);
    }

    private sealed class FakeTailscaleProvider(TailscaleStatusSnapshot status) : ITailscaleStatusProvider
    {
        public int Calls { get; private set; }

        public Task<TailscaleStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(status);
        }
    }
}
