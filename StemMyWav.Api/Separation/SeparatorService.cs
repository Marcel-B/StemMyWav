using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Options;
using StemMyWav.Api.Catalog;
using StemMyWav.Api.Configuration;

namespace StemMyWav.Api.Separation;

/// <summary>Bereitet die Eingabe auf, ruft die Modelle auf und packt die Stems als ZIP.
/// Die Metal-GPU verträgt nur einen Lauf zur Zeit, deshalb die Sperre.</summary>
public sealed class SeparatorService(IProcessRunner runner, IOptions<SeparatorOptions> options)
{
    private readonly SeparatorOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string> RunAsync(string source, string work, SeparationModel model, bool dereverb, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { return await SeparateAsync(source, work, model, dereverb, token); }
        finally { _gate.Release(); }
    }

    private async Task<string> SeparateAsync(string source, string work, SeparationModel model, bool dereverb, CancellationToken token)
    {
        var input = await PrepareAsync(source, work, token);

        var output = Path.Combine(work, "stems");
        Directory.CreateDirectory(output);
        await SeparateWithModelAsync(input, model.Filename, output, token);

        var produced = Directory.GetFiles(output, "*.wav");
        if (produced.Length == 0) throw new InvalidOperationException("Separator produced no WAV stems.");
        var stems = MapStems(model.Stems, produced);

        string? dryVocals = null;
        string? reverb = null;
        if (dereverb)
        {
            var dereverbOutput = Path.Combine(work, "dereverb");
            Directory.CreateDirectory(dereverbOutput);
            await SeparateWithModelAsync(stems["vocals"], _options.DereverbModel, dereverbOutput, token);
            var processed = Directory.GetFiles(dereverbOutput, "*.wav");
            dryVocals = Find(processed, "noreverb");
            reverb = Find(processed, "reverb");
            if (dryVocals is null) throw new InvalidOperationException("De-Reverb model produced no dry vocal stem.");
        }

        var archive = Path.Combine(work, "stems.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            foreach (var stem in model.Stems)
                await AddStemAsync(zip, stems[stem], stem + ".wav", work, token);
            if (dryVocals is not null) await AddStemAsync(zip, dryVocals, "vocals_dry.wav", work, token);
            if (reverb is not null) await AddStemAsync(zip, reverb, "vocals_reverb.wav", work, token);
        }
        return archive;
    }

    /// <summary>Die Modelle rechnen mit Stereo bei 44,1 kHz; alles andere wird vorher umgerechnet.
    /// Eine WAV, die schon passt, geht unverändert weiter — mlx-audio-separator liest sie direkt,
    /// und eine halbe Gigabyte grundlos umzukodieren würde nur Zeit kosten.</summary>
    private async Task<string> PrepareAsync(string source, string work, CancellationToken token)
    {
        var audio = await ProbeAsync(source, token);
        var codec = audio.GetProperty("codec_name").GetString();
        if (codec != "flac" && codec?.StartsWith("pcm_", StringComparison.Ordinal) != true)
            throw new UnreadableInputException("Die Datei enthält keinen FLAC- oder WAV-Audiostrom.");
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

    /// <summary>Ordnet die geschriebenen Dateien den Stems des Katalogs zu. MLX hängt den Stem-Namen
    /// als Klammerausdruck an den Dateinamen, wie er lautet entscheidet aber die Modellkonfiguration:
    /// derselbe Gegenpart heißt mal "(Instrumental)" und mal "(other)". Deshalb wird zuerst über den
    /// Namen zugeordnet, und erst der Rest wird gepaart, wenn genau ein Stem und genau eine Datei
    /// übrig bleiben. Bleibt mehr offen, ist die Zuordnung nicht mehr eindeutig und der Lauf
    /// scheitert, statt einen falsch benannten Stem auszuliefern.</summary>
    private static Dictionary<string, string> MapStems(IReadOnlyList<string> expected, string[] produced)
    {
        var stems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var spare = new List<string>(produced);
        foreach (var stem in expected)
        {
            var hit = spare.FirstOrDefault(path => string.Equals(Label(path), stem, StringComparison.OrdinalIgnoreCase));
            if (hit is null) continue;
            stems[stem] = hit;
            spare.Remove(hit);
        }

        var missing = expected.Where(stem => !stems.ContainsKey(stem)).ToList();
        if (missing.Count == 1 && spare.Count == 1)
        {
            stems[missing[0]] = spare[0];
            missing.Clear();
        }
        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"Expected stems were not produced: {string.Join(", ", missing)}. " +
                $"Produced: {string.Join(", ", produced.Select(path => Label(path) ?? Path.GetFileName(path)))}.");
        return stems;
    }

    /// <summary>Der letzte Klammerausdruck im Dateinamen; bei einem enthallten Gesang ist das
    /// "(noreverb)", das hinter dem "(Vocals)" des ersten Laufs steht.</summary>
    private static string? Label(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var close = name.LastIndexOf(')');
        if (close < 0) return null;
        var open = name.LastIndexOf('(', close);
        return open < 0 ? null : name[(open + 1)..close];
    }

    private static string? Find(string[] stems, string label) =>
        stems.SingleOrDefault(path => string.Equals(Label(path), label, StringComparison.OrdinalIgnoreCase));

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
