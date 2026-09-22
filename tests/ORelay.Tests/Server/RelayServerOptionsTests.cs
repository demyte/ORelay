using ORelay.Server;

namespace ORelay.Tests.Server;

public sealed class RelayServerOptionsTests
{
    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("[::]")]
    public void WildcardBind_RequiresExplicitAdvertisedAddress(string bind)
    {
        var options = new RelayServerOptions { Bind = bind };

        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void PublicUrlAlreadyAtCallbackPath_IsNotAppendedTwice()
    {
        var options = new RelayServerOptions
        {
            PublicUrl = new Uri("https://relay.example.test/callback"),
        };

        options.Validate();

        Assert.Equal("https://relay.example.test/callback", options.RelayCallbackUrl);
    }

    [Fact]
    public void ExplicitSharedBind_AllowsNonLoopbackDestination()
    {
        var options = new RelayServerOptions
        {
            Bind = "0.0.0.0",
            Hostname = "relay.example.test",
        };

        options.Validate();

        Assert.True(options.AllowsNonLoopbackDestinations);
        Assert.Equal("http://relay.example.test:12987/callback", options.RelayCallbackUrl);
    }

    [Fact]
    public void LoopbackIpv6Bind_IsPreservedInAdvertisedCallback()
    {
        var options = new RelayServerOptions { Bind = "::1" };

        options.Validate();

        Assert.Equal("http://[::1]:12987/callback", options.RelayCallbackUrl);
    }

    [Fact]
    public void DnsBind_IsRejectedBecauseKestrelTreatsItAsWildcard()
    {
        var options = new RelayServerOptions { Bind = "relay.example.test" };

        var exception = Assert.Throws<ArgumentException>(options.Validate);

        Assert.Contains("Use Hostname or PublicUrl", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PublicUrlWithPrefixAndCallbackPath_IsPreservedAndMappedAsFullPath()
    {
        var options = new RelayServerOptions
        {
            PublicUrl = new Uri("https://relay.example.test/oauth/callback"),
        };

        options.Validate();

        Assert.Equal("https://relay.example.test/oauth/callback", options.RelayCallbackUrl);
        Assert.Equal("/oauth/callback", options.CallbackRoutePath);
    }

    [Fact]
    public void PublicUrlWithPrefixBase_AppendsCallbackPath()
    {
        var options = new RelayServerOptions
        {
            PublicUrl = new Uri("https://relay.example.test/oauth"),
        };

        options.Validate();

        Assert.Equal("https://relay.example.test/oauth/callback", options.RelayCallbackUrl);
        Assert.Equal("/oauth/callback", options.CallbackRoutePath);
    }
}
