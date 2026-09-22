using Microsoft.Extensions.Configuration;
using ORelay.Aspire.Hosting;
using ORelay.Discovery;

var builder = DistributedApplication.CreateBuilder(args);
var server = new Uri(builder.Configuration["RelayUrl"] ?? "http://localhost:12987");
var relay = builder.AddORelay("relay", server);
var provider = builder.AddProject<Projects.ORelay_Sample_Provider>("provider")
    .WithHttpEndpoint(name: "http")
    .WithEnvironment("RelayRedirectUri", builder.Configuration["RelayRedirectUri"] ?? new Uri(server, "/callback").AbsoluteUri);
builder.AddProject<Projects.ORelay_Sample_Api>("api")
    .WithHttpEndpoint(port: builder.Configuration.GetValue<int?>("ApiPort"), name: "http")
    .WithEnvironment("ProviderUrl", provider.GetEndpoint("http"))
    .WithORelay(relay, "/oauth/callback",
        callbackUrl: builder.Configuration["CallbackUrl"] is { } callbackUrl ? new Uri(callbackUrl) : null,
        callbackOptions: new()
        {
            Hostname = builder.Configuration["CallbackHostname"],
            AutoDiscovery = builder.Configuration.GetValue<CallbackDiscoveryMode>("CallbackDiscovery"),
        });
await builder.Build().RunAsync();

public partial class Program;
