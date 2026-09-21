using System.Security.Cryptography;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 512L * 1024 * 1024);
builder.Services.AddSingleton<JobStore>();
builder.Services.AddHttpClient("mac", client => client.Timeout = TimeSpan.FromMinutes(30));
builder.Services.AddHostedService<JobWorker>();
var app = builder.Build();

var clientKey = Secrets.Read(builder.Configuration, "Gateway:ApiKey");
var macUrl = builder.Configuration["MacBackend:Url"];
if (!Uri.TryCreate(macUrl, UriKind.Absolute, out var backend) ||
    (backend.Scheme != Uri.UriSchemeHttps && !(app.Environment.IsDevelopment() && backend.IsLoopback && backend.Scheme == Uri.UriSchemeHttp)))
    throw new InvalidOperationException("MacBackend:Url muss eine HTTPS-URL sein (z. B. Tailscale Serve).");
_ = Secrets.Read(builder.Configuration, "MacBackend:ApiKey");

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api") && !ValidKey(context.Request.Headers["X-Api-Key"].ToString(), clientKey))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }
    await next();
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapPost("/api/jobs", async (HttpContext context, JobStore store, bool? dereverb) =>
{
    if (context.Request.ContentType is not ("audio/flac" or "audio/x-flac"))
        return Results.Problem("Content-Type muss audio/flac sein.", statusCode: 415);
    var job = await store.CreateAsync(context.Request.Body, dereverb == true, context.RequestAborted);
    return Results.Accepted($"/api/jobs/{job.Id}", new { job.Id, job.Status });
});
app.MapGet("/api/jobs/{id:guid}", (Guid id, JobStore store) =>
    store.Read(id) is { } job
        ? Results.Ok(new { job.Id, job.Status, job.Attempts, job.LastError, job.CreatedUtc, job.UpdatedUtc })
        : Results.NotFound());
app.MapGet("/api/jobs/{id:guid}/result", (Guid id, JobStore store) =>
{
    var job = store.Read(id);
    if (job is null) return Results.NotFound();
    if (job.Status != "completed") return Results.Problem("Ergebnis noch nicht verfügbar.", statusCode: 409);
    return Results.File(store.ResultPath(id), "application/zip", "stems.zip");
});
app.Run();

static bool ValidKey(string supplied, string expected)
{
    var left = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
    var right = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
    return CryptographicOperations.FixedTimeEquals(left, right);
}
