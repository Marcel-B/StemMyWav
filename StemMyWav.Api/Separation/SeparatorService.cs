using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Options;
using StemMyWav.Api.Configuration;

namespace StemMyWav.Api.Separation;

/// <summary>Bereitet die Eingabe auf, ruft die Modelle auf und packt die Stems als ZIP.
/// Die Metal-GPU verträgt nur einen Lauf zur Zeit, deshalb die Sperre.</summary>
public sealed class SeparatorService(IProcessRunner runner, IOptions<SeparatorOptions> options)
{
    private readonly SeparatorOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string> RunAsync(string source, string work, bool dereverb, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { return await SeparateAsync(source, work, dereverb, token); }
        finally { _gate.Release(); }
    }

    private async Task<string> SeparateAsync(string source, string work, bool dereverb, CancellationToken token)
    {
        var input = await PrepareAsync(source, work, token);

        var output = Path.Combine(work, "stems");
        Directory.CreateDirectory(output);
        await SeparateWithModelAsync(input, _options.Model, output, token);

        var stems = Directory.GetFiles(output, "*.wav");
        if (stems.Length == 0) throw new InvalidOperationException("Separator produced no WAV stems.");
        var vocals = Find(stems, "(Vocals)");
        var instrumental = Find(stems, "(Instrumental)");
        if (vocals is null || instrumental is null)
            throw new InvalidOperationException("Expected vocals and instrumental stems were not produced.");

        string? dryVocals = null;
        string? reverb = null;
        if (dereverb)
        {
            var dereverbOutput = Path.Combine(work, "dereverb");
            Directory.CreateDirectory(dereverbOutput);
            await SeparateWithModelAsync(vocals, _options.DereverbModel, dereverbOutput, token);
            var processed = Directory.GetFiles(dereverbOutput, "*.wav");
            dryVocals = Find(processed, "(noreverb)");
            reverb = Find(processed, "(reverb)");
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

    /// <summary>MLX erwartet Stereo bei 44,1 kHz; alles andere wird vorher umgerechnet.</summary>
    private async Task<string> PrepareAsync(string source, string work, CancellationToken token)
    {
        var audio = await ProbeAsync(source, token);
        if (audio.GetProperty("codec_name").GetString() != "flac")
            throw new UnreadableInputException("Die Datei enthält keinen FLAC-Audiostrom.");
        if (audio.GetProperty("sample_rate").GetString() == "44100" && audio.GetProperty("channels").GetInt32() == 2)
            return source;

        var prepared = Path.Combine(work, "prepared.flac");
        await runner.RunAsync("ffmpeg",
            ["-hide_banner", "-loglevel", "error", "-i", source, "-vn", "-ac", "2", "-ar", "44100", "-c:a", "flac", prepared],
            token);
        return prepared;
    }

    private const string Unreadable = "Die Datei ließ sich nicht lesen; sie ist vermutlich unvollständig oder beschädigt.";

    /// <summary>Liest den ersten Audiostrom. Scheitert ffprobe, ist die Datei unbrauchbar —
    /// etwa abgeschnitten — und nicht der Dienst gestört.</summary>
    private async Task<JsonElement> ProbeAsync(string source, CancellationToken token)
    {
        string probe;
        try
        {
            probe = await runner.RunAsync("ffprobe",
                ["-v", "error", "-select_streams", "a:0", "-show_entries", "stream=codec_name,sample_rate,channels", "-of", "json", source],
                token);
        }
        catch (InvalidOperationException error)
        {
            throw new UnreadableInputException(Unreadable, error);
        }

        try
        {
            using var document = JsonDocument.Parse(probe);
            var streams = document.RootElement.GetProperty("streams");
            if (streams.GetArrayLength() == 0) throw new UnreadableInputException("Die Datei enthält keine Audiospur.");
            return streams[0].Clone();
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException)
        {
            throw new UnreadableInputException(Unreadable, error);
        }
    }

    private Task SeparateWithModelAsync(string input, string model, string output, CancellationToken token)
    {
        var arguments = new List<string>
        {
            input, "--model_filename", model, "--output_dir", output, "--output_format", "WAV"
        };
        if (_options.ModelDirectory is { Length: > 0 } directory)
        {
            arguments.Add("--model_file_dir");
            arguments.Add(directory);
        }
        return runner.RunAsync(_options.Executable, arguments, token);
    }

    private static string? Find(string[] stems, string marker) =>
        stems.SingleOrDefault(path => Path.GetFileName(path).Contains(marker, StringComparison.OrdinalIgnoreCase));

    /// <summary>Logic erwartet 48 kHz und 16 Bit, die Modelle liefern 44,1 kHz.</summary>
    private async Task AddStemAsync(ZipArchive zip, string source, string name, string work, CancellationToken token)
    {
        var converted = Path.Combine(work, name);
        await runner.RunAsync("ffmpeg",
            ["-hide_banner", "-loglevel", "error", "-i", source,
             "-map", "0:a:0", "-ac", "2", "-ar", "48000", "-c:a", "pcm_s16le", converted],
            token);
        zip.CreateEntryFromFile(converted, name, CompressionLevel.NoCompression);
        File.Delete(converted);
    }
}
