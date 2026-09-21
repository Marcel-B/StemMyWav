using Microsoft.Extensions.Options;
using StemMyWav.Api.Configuration;
using StemMyWav.Api.Http;
using StemMyWav.Api.Separation;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 512L * 1024 * 1024);

builder.Services.AddOptions<MacApiOptions>()
    .Bind(builder.Configuration.GetSection(MacApiOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<SeparatorOptions>()
    .Bind(builder.Configuration.GetSection(SeparatorOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
builder.Services.AddSingleton<SeparatorService>();

var app = builder.Build();

app.UseApiKey(app.Services.GetRequiredService<IOptions<MacApiOptions>>().Value.Key!);
app.MapSeparationEndpoints();
app.Run();
