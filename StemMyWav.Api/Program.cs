using Microsoft.Extensions.Options;
using StemMyWav.Api.Catalog;
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

builder.Services.AddSingleton(ModelCatalog.Load());
builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
builder.Services.AddSingleton<SeparatorService>();
builder.Services.AddSingleton(new Workspaces(Path.GetTempPath()));
builder.Services.AddHostedService<WorkspaceCleaner>();

var app = builder.Build();

// Eine unbekannte Voreinstellung soll den Start abbrechen und nicht erst beim ersten Auftrag auffallen.
app.Services.GetRequiredService<ModelCatalog>()
    .ResolveDefault(app.Services.GetRequiredService<IOptions<SeparatorOptions>>().Value.DefaultModel);

app.UseApiKey(app.Services.GetRequiredService<IOptions<MacApiOptions>>().Value.Key!);
app.MapSeparationEndpoints();
app.Run();
