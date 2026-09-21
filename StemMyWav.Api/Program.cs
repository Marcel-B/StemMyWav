using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 512L * 1024 * 1024);
builder.Services.AddSingleton<SeparatorService>();
var app = builder.Build();
var apiKey = builder.Configuration["MacApi:Key"];
if (string.IsNullOrWhiteSpace(apiKey))
    throw new InvalidOperationException("MacApi:Key muss gesetzt sein.");

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api") &&
        !ValidKey(context.Request.Headers["X-Api-Key"].ToString(), apiKey))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }
    await next();
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapPost("/api/separate", async (HttpContext context, SeparatorService service, bool? dereverb) =>
{
    if (context.Request.ContentType is not ("audio/flac" or "audio/x-flac"))
        return Results.Problem("Content-Type muss audio/flac sein.", statusCode: 415);

    var work = Path.Combine(Path.GetTempPath(), "stemmywav-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(work);
    var keepUntilSent = false;
    try
    {
        var input = Path.Combine(work, "input.flac");
        await using (var file = File.Create(input))
            await context.Request.Body.CopyToAsync(file, context.RequestAborted);
        await using (var file = File.OpenRead(input))
        {
            var header = new byte[4];
            if (await file.ReadAsync(header, context.RequestAborted) != 4 || !header.AsSpan().SequenceEqual("fLaC"u8))
                return Results.Problem("Ungültige FLAC-Datei.", statusCode: 400);
        }
        var archive = await service.RunAsync(input, work, dereverb == true, context.RequestAborted);
        context.Response.OnCompleted(() => { Directory.Delete(work, true); return Task.CompletedTask; });
        keepUntilSent = true;
        return Results.File(archive, "application/zip", "stems.zip");
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        return Results.StatusCode(499);
    }
    catch (Exception error)
    {
        app.Logger.LogError(error, "Separation failed");
        return Results.Problem("Stem-Separation fehlgeschlagen. Server-Logs prüfen.", statusCode: 500);
    }
    finally { if (!keepUntilSent) Directory.Delete(work, true); }
});
app.Run();

static bool ValidKey(string supplied, string expected)
{
    var left = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
    var right = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
    return CryptographicOperations.FixedTimeEquals(left, right);
}

public sealed class SeparatorService(IConfiguration config)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string> RunAsync(string source, string work, bool dereverb, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var probe = await ExecAsync("ffprobe", ["-v", "error", "-select_streams", "a:0", "-show_entries", "stream=codec_name,sample_rate,channels", "-of", "json", source], token);
            using var json = JsonDocument.Parse(probe);
            var audio = json.RootElement.GetProperty("streams")[0];
            if (audio.GetProperty("codec_name").GetString() != "flac") throw new InvalidDataException("Input is not FLAC.");
            var input = source;
            if (audio.GetProperty("sample_rate").GetString() != "44100" || audio.GetProperty("channels").GetInt32() != 2)
            {
                input = Path.Combine(work, "prepared.flac");
                await ExecAsync("ffmpeg", ["-hide_banner", "-loglevel", "error", "-i", source, "-vn", "-ac", "2", "-ar", "44100", "-c:a", "flac", input], token);
            }
            var output = Path.Combine(work, "stems");
            Directory.CreateDirectory(output);
            var arguments = new List<string> { input, "--model_filename", config["Separator:Model"] ?? "model_bs_roformer_ep_317_sdr_12.9755.ckpt", "--output_dir", output, "--output_format", "WAV" };
            if (config["Separator:ModelDirectory"] is { Length: > 0 } modelDirectory)
            {
                arguments.Add("--model_file_dir");
                arguments.Add(modelDirectory);
            }
            await ExecAsync(config["Separator:Executable"] ?? "mlx-audio-separator", [.. arguments], token);
            var stems = Directory.GetFiles(output, "*.wav");
            if (stems.Length == 0) throw new InvalidOperationException("Separator produced no WAV stems.");
            var vocals = stems.SingleOrDefault(path => Path.GetFileName(path).Contains("(Vocals)", StringComparison.OrdinalIgnoreCase));
            var instrumental = stems.SingleOrDefault(path => Path.GetFileName(path).Contains("(Instrumental)", StringComparison.OrdinalIgnoreCase));
            if (vocals is null || instrumental is null) throw new InvalidOperationException("Expected vocals and instrumental stems were not produced.");

            string? dryVocals = null;
            string? reverb = null;
            if (dereverb)
            {
                var dereverbOutput = Path.Combine(work, "dereverb");
                Directory.CreateDirectory(dereverbOutput);
                var dereverbArgs = new List<string> { vocals, "--model_filename", config["Separator:DereverbModel"] ?? "dereverb_mel_band_roformer_anvuew_sdr_19.1729.ckpt", "--output_dir", dereverbOutput, "--output_format", "WAV" };
                if (config["Separator:ModelDirectory"] is { Length: > 0 } cache)
                {
                    dereverbArgs.Add("--model_file_dir");
                    dereverbArgs.Add(cache);
                }
                await ExecAsync(config["Separator:Executable"] ?? "mlx-audio-separator", [.. dereverbArgs], token);
                var processed = Directory.GetFiles(dereverbOutput, "*.wav");
                dryVocals = processed.SingleOrDefault(path => Path.GetFileName(path).Contains("(noreverb)", StringComparison.OrdinalIgnoreCase));
                reverb = processed.SingleOrDefault(path => Path.GetFileName(path).Contains("(reverb)", StringComparison.OrdinalIgnoreCase));
                if (dryVocals is null) throw new InvalidOperationException("De-Reverb model produced no dry vocal stem.");
            }
            var archive = Path.Combine(work, "stems.zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            {
                await AddStemAsync(zip, vocals, "vocals.wav", work, token);
                await AddStemAsync(zip, instrumental, "instrumental.wav", work, token);
                if (dryVocals is not null) await AddStemAsync(zip, dryVocals, "vocals_dry.wav", work, token);
                if (reverb is not null) await AddStemAsync(zip, reverb, "vocals_reverb.wav", work, token);
            }
            return archive;
        }
        finally { _gate.Release(); }
    }

    private static async Task AddStemAsync(ZipArchive zip, string source, string name, string work, CancellationToken token)
    {
        var converted = Path.Combine(work, name);
        await ExecAsync("ffmpeg", ["-hide_banner", "-loglevel", "error", "-i", source,
            "-map", "0:a:0", "-ac", "2", "-ar", "48000", "-c:a", "pcm_s16le", converted], token);
        zip.CreateEntryFromFile(converted, name, CompressionLevel.NoCompression);
        File.Delete(converted);
    }

    private static async Task<string> ExecAsync(string executable, string[] args, CancellationToken token)
    {
        var info = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Cannot start {executable}.");
        using var cancellation = token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        var result = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException($"{executable} exited {process.ExitCode}: {error}");
        return result;
    }
}
