using ORelay.Server;

namespace ORelay.Tests.Server;

public sealed class CallbackDestinationTests
{
    [Theory]
    [InlineData("http://127.0.0.1:4100/callback")]
    [InlineData("https://[::1]:4100/callback")]
    [InlineData("http://localhost:4100/callback")]
    public void LoopbackHttpDestinations_AreAccepted(string callbackUrl)
    {
        Assert.True(CallbackDestination.TryValidate(callbackUrl, allowNonLoopback: false, out _, out _));
    }

    [Theory]
    [InlineData("http://127.0.0.1:4100/callback?existing=query")]
    [InlineData("http://127.0.0.1:4100/callback?")]
    [InlineData("http://user:password@127.0.0.1:4100/callback")]
    [InlineData("https://127.0.0.1:4100/callback#fragment")]
    [InlineData("https://127.0.0.1:4100/callback#")]
    [InlineData("ftp://127.0.0.1:4100/callback")]
    [InlineData("http://127.0.0.1:4100/callback\r\n")]
    [InlineData(" http://127.0.0.1:4100/callback")]
    [InlineData("http://0.0.0.0:4100/callback")]
    public void Destinations_WithUnusableParts_AreRejected(string callbackUrl)
    {
        Assert.False(CallbackDestination.TryValidate(callbackUrl, allowNonLoopback: true, out _, out _));
    }

    [Fact]
    public void NonLoopbackDestination_RequiresExplicitSharedMode()
    {
        Assert.False(CallbackDestination.TryValidate("http://192.0.2.10:4100/callback", allowNonLoopback: false, out _, out var error));
        Assert.Equal("callback_must_be_loopback", error);
        Assert.True(CallbackDestination.TryValidate("http://192.0.2.10:4100/callback", allowNonLoopback: true, out _, out _));
    }
}
