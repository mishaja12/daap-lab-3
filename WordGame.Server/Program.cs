using Microsoft.AspNetCore.Server.Kestrel.Core;
using WordGame.Server;
using WordGame.Server.Networking;
using WordGame.Server.Security;
using WordGame.Server.Services;

var builder = WebApplication.CreateBuilder(args);
var nodeOptions = builder.Configuration.GetSection("Node").Get<NodeRuntimeOptions>() ?? new NodeRuntimeOptions();
var http1Port = builder.Configuration.GetValue<int?>("Ports:Http1") ?? 5119;
var http2Port = builder.Configuration.GetValue<int?>("Ports:Http2") ?? 5118;

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(http1Port, listen => listen.Protocols = HttpProtocols.Http1);
    options.ListenLocalhost(http2Port, listen => listen.Protocols = HttpProtocols.Http2);
});

builder.Services.AddGrpc();
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddSingleton<WordValidator>();
builder.Services.AddSingleton(nodeOptions);
builder.Services.AddSingleton<MessageCrypto>();
builder.Services.AddSingleton<NodeRegistryStore>();
builder.Services.AddSingleton<PeerNodeManager>();
builder.Services.AddSingleton<PeerGameEngine>();
builder.Services.AddScoped<GameClientService>();

var app = builder.Build();

app.UseStaticFiles();
app.UseRouting();

if (nodeOptions.EnableRegistry)
{
    app.MapGrpcService<NodeRegistryService>();
}

app.MapGrpcService<PeerGameService>();
app.MapBlazorHub();
app.MapRazorPages();
app.MapFallbackToPage("/Host");

app.Run();
