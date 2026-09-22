using StemMyWav.Api.Separation;

namespace StemMyWav.Api.Tests;

/// <summary>Ersetzt ffprobe, FFmpeg und die MLX-CLI. ffprobe liefert eine einstellbare Antwort,
/// FFmpeg legt seine Zieldatei an und der Separator schreibt die eingestellten Stems.</summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    public List<Invocation> Calls { get; } = [];

    public string ProbeResult { get; set; } = Probe("flac", "44100", 2);

    /// <summary>Lässt ffprobe scheitern, so wie bei einer abgeschnittenen Datei.</summary>
    public string? ProbeFailure { get; set; }

    /// <summary>Die Marker, die der Separator je Modell in den Ausgabeordner schreibt.</summary>
    public Func<string, string[]> StemsFor { get; set; } =
        model => model.Contains("dereverb", StringComparison.OrdinalIgnoreCase)
            ? ["(noreverb)", "(reverb)"]
            : ["(Vocals)", "(Instrumental)"];

    /// <summary>Wird vor jedem Aufruf ausgeführt, etwa um Gleichzeitigkeit zu beobachten.</summary>
    public Func<string, Task>? Before { get; set; }

    public static string Probe(string codec, string sampleRate, int channels) =>
        $$"""{"streams":[{"codec_name":"{{codec}}","sample_rate":"{{sampleRate}}","channels":{{channels}}}]}""";

    public async Task<string> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
    {
        if (Before is not null) await Before(executable);
        Calls.Add(new Invocation(executable, [.. arguments]));

        if (executable == "ffprobe")
            return ProbeFailure is null
                ? ProbeResult
                : throw new InvalidOperationException($"ffprobe exited 1: {ProbeFailure}");

        if (executable == "ffmpeg")
        {
            File.WriteAllBytes(arguments[^1], "RIFFfake"u8.ToArray());
            return string.Empty;
        }

        var model = Value(arguments, "--model_filename");
        var output = Value(arguments, "--output_dir");
        var stem = Path.GetFileNameWithoutExtension(arguments[0]);
        foreach (var marker in StemsFor(model))
            File.WriteAllBytes(Path.Combine(output, $"{stem}_{marker}_{model}.wav"), "RIFFfake"u8.ToArray());
        return string.Empty;
    }

    private static string Value(IReadOnlyList<string> arguments, string name)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
            if (arguments[index] == name) return arguments[index + 1];
        throw new InvalidOperationException($"{name} was not passed.");
    }

    public Invocation Single(string executable) => Calls.Single(call => call.Executable == executable);
    public IEnumerable<Invocation> All(string executable) => Calls.Where(call => call.Executable == executable);

    internal sealed record Invocation(string Executable, string[] Arguments)
    {
        public bool Has(params string[] sequence)
        {
            for (var index = 0; index + sequence.Length <= Arguments.Length; index++)
                if (Arguments.Skip(index).Take(sequence.Length).SequenceEqual(sequence))
                    return true;
            return false;
        }
    }
}
