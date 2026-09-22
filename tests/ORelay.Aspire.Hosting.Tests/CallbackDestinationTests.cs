using System.Net.Sockets;
using Aspire.Hosting.ApplicationModel;
using ORelay.Discovery;
using Xunit;

namespace ORelay.Aspire.Hosting.Tests;

public sealed class CallbackDestinationTests
{
    [Fact]
    public async Task ExplicitUrlWorksWithoutEndpointOrTailscaleEvenWithOtherOptions()
    {
        var tailscale = new StatusProvider(TailscaleStatusSnapshot.Unavailable("Unavailable."));
        var result = await CallbackDestination.ResolveAsync(null, "/ignored", new Uri("https://callback.example.test/exact"),
            new() { Hostname = "ignored.test", AutoDiscovery = CallbackDiscoveryMode.Tailscale, TailscaleProvider = tailscale }, default);
        Assert.Equal("https://callback.example.test/exact", result.AbsoluteUri);
        Assert.Equal(0, tailscale.Calls);
    }

    [Fact]
    public async Task HostnameOverridePreservesAllocatedPortAndCallbackPathAndSkipsDiscovery()
    {
        var tailscale = new StatusProvider(TailscaleStatusSnapshot.Unavailable("Unavailable."));
        var endpoint = Endpoint(EndpointBindingMode.IPv4AnyAddresses);
        var result = await CallbackDestination.ResolveAsync(endpoint, "/oauth/complete", null,
            new() { Hostname = "worktree.example.test", AutoDiscovery = CallbackDiscoveryMode.Tailscale, TailscaleProvider = tailscale }, default);
        Assert.Equal("http://worktree.example.test:43210/oauth/complete", result.AbsoluteUri);
        Assert.Equal(0, tailscale.Calls);
    }

    [Fact]
    public async Task TailscaleSelectionUsesWorktreeProviderAndActualAspireAllocation()
    {
        var tailscale = new StatusProvider(new(true, "Running", "worktree.tail.example.", "worktree", ["100.101.102.103"]));
        var result = await CallbackDestination.ResolveAsync(Endpoint(EndpointBindingMode.IPv4AnyAddresses), "/oauth/callback", null,
            new() { AutoDiscovery = CallbackDiscoveryMode.Tailscale, TailscaleProvider = tailscale }, default);
        Assert.Equal("http://worktree.tail.example:43210/oauth/callback", result.AbsoluteUri);
        Assert.Equal(1, tailscale.Calls);
    }

    [Fact]
    public async Task RemoteDiscoveryRejectsKnownLoopbackOnlyAspireBinding()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CallbackDestination.ResolveAsync(
            Endpoint(EndpointBindingMode.SingleAddress), "/oauth/callback", null, new() { Hostname = "worktree.example.test" }, default));
        Assert.Contains("IncompatibleBinding", error.Message, StringComparison.Ordinal);
        Assert.Contains("Bind the application", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AmbiguousTailscaleStatusReportsCandidatesInsteadOfRegisteringGuessedHost()
    {
        var tailscale = new StatusProvider(new(true, "Running", null, null, ["100.101.102.103", "fd7a:115c:a1e0::1"]));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CallbackDestination.ResolveAsync(
            Endpoint(EndpointBindingMode.IPv4AnyAddresses), "/oauth/callback", null,
            new() { AutoDiscovery = CallbackDiscoveryMode.Tailscale, TailscaleProvider = tailscale }, default));
        Assert.Contains("TailscaleAmbiguous", error.Message, StringComparison.Ordinal);
        Assert.Contains("100.101.102.103", error.Message, StringComparison.Ordinal);
        Assert.Contains("explicit", error.Message, StringComparison.Ordinal);
    }

    private static EndpointReference Endpoint(EndpointBindingMode binding)
    {
        var resource = new ExecutableResource("api", "unused", ".");
        var annotation = new EndpointAnnotation(ProtocolType.Tcp, uriScheme: "http", name: "http");
        annotation.AllocatedEndpoint = new AllocatedEndpoint(annotation, "127.0.0.1", 43210, binding, "43210");
        resource.Annotations.Add(annotation);
        return new EndpointReference(resource, "http");
    }

    private sealed class StatusProvider(TailscaleStatusSnapshot status) : ITailscaleStatusProvider
    {
        internal int Calls { get; private set; }
        public Task<TailscaleStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(status);
        }
    }
}
