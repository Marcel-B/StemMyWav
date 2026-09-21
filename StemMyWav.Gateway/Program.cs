using Microsoft.Extensions.Options;
using StemMyWav.Gateway.Http;
using StemMyWav.Gateway.Configuration;
using StemMyWav.Gateway.Jobs;
using StemMyWav.Gateway.OpenApi;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;
var environment = builder.Environment;

builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 512L * 1024 * 1024);

builder.Services.AddOptions<GatewayOptions>()
    .Bind(configuration.GetSection(GatewayOptions.Section))
    .PostConfigure(options => options.ApiKey = Secrets.Read(configuration, "Gateway:ApiKey"))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<MacBackendOptions>()
    .Bind(configuration.GetSection(MacBackendOptions.Section))
    .PostConfigure(options => options.ApiKey = Secrets.Read(configuration, "MacBackend:ApiKey"))
    .Validate(options => IsReachableBackend(options.Url),
        "MacBackend:Url muss eine HTTPS-URL sein (z. B. Tailscale Serve).")
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<JobStore>();
builder.Services.AddHttpClient("mac", client => client.Timeout = TimeSpan.FromMinutes(30));
builder.Services.AddHostedService<JobWorker>();
builder.Services.AddOpenApi(options => options.AddDocumentTransformer<GatewayDocumentTransformer>());

var app = builder.Build();

app.UseApiKey(app.Services.GetRequiredService<IOptions<GatewayOptions>>().Value.ApiKey!);
app.MapOpenApi();
app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "StemMyWav Gateway v1"));
app.MapGatewayEndpoints();
app.Run();

// Nur im Tailnet soll der Gateway sprechen; unverschlüsselt ist ausschließlich die
// lokale Entwicklung gegen einen Stub erlaubt.
bool IsReachableBackend(string? url) =>
    Uri.TryCreate(url, UriKind.Absolute, out var backend) &&
    (backend.Scheme == Uri.UriSchemeHttps ||
     (environment.IsDevelopment() && backend.IsLoopback && backend.Scheme == Uri.UriSchemeHttp));
