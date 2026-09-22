using System.IO.Compression;
using Microsoft.Extensions.Options;
using StemMyWav.Api.Catalog;
using StemMyWav.Api.Configuration;
using StemMyWav.Api.Separation;

namespace StemMyWav.Api.Tests;

public sealed class SeparatorServiceTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "stemmywav-test-" + Guid.NewGuid().ToString("N"));
    private readonly FakeProcessRunner _runner = new();

    public SeparatorServiceTests() => Directory.CreateDirectory(_work);

    public void Dispose() => Directory.Delete(_work, true);

    private SeparatorService Create(string? modelDirectory = null) =>
        new(_runner, Options.Create(new SeparatorOptions
        {
            Executable = "mlx-audio-separator",
            DereverbModel = "dereverb_roformer.ckpt",
            ModelDirectory = modelDirectory
        }));

    private static SeparationModel Model(string filename = "bs_roformer.ckpt", params string[] stems) =>
        new()
        {
            Id = "test-model",
            Name = "Test",
            Filename = filename,
            Family = "Test",
            Task = ModelTask.Vocals,
            Stems = stems.Length > 0 ? stems : ["vocals", "instrumental"],
            Speed = ModelSpeed.Fast
        };

    private string Source(string extension = "flac")
    {
        var source = Path.Combine(_work, "input." + extension);
        File.WriteAllBytes(source, "fLaCfake"u8.ToArray());
        return source;
    }

    private static string[] Entries(string archive)
    {
        using var zip = ZipFile.OpenRead(archive);
        return [.. zip.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal)];
    }

    [Fact]
    public async Task Packs_vocals_and_instrumental_as_forty_eight_kilohertz_stereo()
    {
        var archive = await Create().RunAsync(Source(), _work, Model(), dereverb: false, CancellationToken.None);

        Assert.Equal(["instrumental.wav", "vocals.wav"], Entries(archive));
        foreach (var conversion in _runner.All("ffmpeg"))
            Assert.True(conversion.Has("-ac", "2", "-ar", "48000", "-c:a", "pcm_s16le"),
                "Jeder Stem muss auf 48 kHz, 16 Bit und Stereo konvertiert werden.");
    }

    [Fact]
    public async Task Loads_the_model_file_of_the_chosen_model()
    {
        await Create().RunAsync(Source(), _work, Model("htdemucs_6s.yaml"), dereverb: false, CancellationToken.None);

        Assert.True(_runner.Single("mlx-audio-separator").Has("--model_filename", "htdemucs_6s.yaml"));
    }

    [Fact]
    public async Task Packs_every_stem_a_six_stem_model_produces()
    {
        string[] stems = ["vocals", "drums", "bass", "guitar", "piano", "other"];
        _runner.StemsFor = _ => [.. stems.Select(stem => $"({stem})")];

        var archive = await Create().RunAsync(Source(), _work, Model("htdemucs_6s.yaml", stems),
            dereverb: false, CancellationToken.None);

        Assert.Equal(["bass.wav", "drums.wav", "guitar.wav", "other.wav", "piano.wav", "vocals.wav"], Entries(archive));
    }

    [Fact]
    public async Task Names_the_counterpart_of_a_two_stem_model_after_the_catalog()
    {
        // Manche Modellkonfigurationen nennen das Instrumental "other"; im ZIP muss es trotzdem
        // unter dem Namen liegen, den der Katalog nennt.
        _runner.StemsFor = _ => ["(vocals)", "(other)"];

        var archive = await Create().RunAsync(Source(), _work, Model(), dereverb: false, CancellationToken.None);

        Assert.Equal(["instrumental.wav", "vocals.wav"], Entries(archive));
    }

    [Fact]
    public async Task Prepares_the_input_when_it_is_not_stereo_at_forty_four_one()
    {
        _runner.ProbeResult = FakeProcessRunner.Probe("flac", "48000", 1);

        await Create().RunAsync(Source(), _work, Model(), dereverb: false, CancellationToken.None);

        var prepare = _runner.All("ffmpeg").First();
        Assert.True(prepare.Has("-ac", "2", "-ar", "44100", "-c:a", "flac"));
        Assert.EndsWith("prepared.flac", prepare.Arguments[^1]);
        Assert.Equal(Path.Combine(_work, "prepared.flac"), _runner.Single("mlx-audio-separator").Arguments[0]);
    }

    [Fact]
    public async Task Passes_the_original_input_when_it_already_matches()
    {
        var source = Source();

        await Create().RunAsync(source, _work, Model(), dereverb: false, CancellationToken.None);

        Assert.DoesNotContain(_runner.All("ffmpeg"), call => call.Has("-ar", "44100"));
        Assert.Equal(source, _runner.Single("mlx-audio-separator").Arguments[0]);
    }

    [Fact]
    public async Task Passes_a_wav_that_already_matches_straight_to_the_model()
    {
        // Eine halbe Gigabyte grundlos umzukodieren würde nur Zeit kosten; MLX liest WAV selbst.
        _runner.ProbeResult = FakeProcessRunner.Probe("pcm_s16le", "44100", 2);
        var source = Source("wav");

        await Create().RunAsync(source, _work, Model(), dereverb: false, CancellationToken.None);

        Assert.Equal(source, _runner.Single("mlx-audio-separator").Arguments[0]);
    }

    [Fact]
    public async Task Prepares_a_wav_that_is_not_stereo_at_forty_four_one()
    {
        _runner.ProbeResult = FakeProcessRunner.Probe("pcm_s24le", "48000", 2);

        await Create().RunAsync(Source("wav"), _work, Model(), dereverb: false, CancellationToken.None);

        Assert.True(_runner.All("ffmpeg").First().Has("-ac", "2", "-ar", "44100", "-c:a", "flac"));
        Assert.Equal(Path.Combine(_work, "prepared.flac"), _runner.Single("mlx-audio-separator").Arguments[0]);
    }

    [Fact]
    public async Task Rejects_input_that_is_neither_flac_nor_wav()
    {
        _runner.ProbeResult = FakeProcessRunner.Probe("mp3", "44100", 2);

        var error = await Assert.ThrowsAsync<UnreadableInputException>(() =>
            Create().RunAsync(Source(), _work, Model(), dereverb: false, CancellationToken.None));
        Assert.Contains("FLAC", error.Message);
        Assert.Contains("WAV", error.Message);
    }

    [Fact]
    public async Task Reports_a_truncated_file_as_a_caller_error()
    {
        // Genau der Fall aus dem Betrieb: die Magic Bytes stimmen, danach bricht die Datei ab.
        _runner.ProbeFailure = "input.flac: End of file";

        var error = await Assert.ThrowsAsync<UnreadableInputException>(() =>
            Create().RunAsync(Source(), _work, Model(), dereverb: false, CancellationToken.None));
        Assert.Contains("unvollständig", error.Message);
    }

    [Fact]
    public async Task Reports_a_file_without_an_audio_track_as_a_caller_error()
    {
        _runner.ProbeResult = """{"streams":[]}""";

        await Assert.ThrowsAsync<UnreadableInputException>(() =>
            Create().RunAsync(Source(), _work, Model(), dereverb: false, CancellationToken.None));
    }

    [Fact]
    public async Task Reports_unreadable_probe_output_as_a_caller_error()
    {
        _runner.ProbeResult = "not json";

        await Assert.ThrowsAsync<UnreadableInputException>(() =>
            Create().RunAsync(Source(), _work, Model(), dereverb: false, CancellationToken.None));
    }

    [Fact]
    public async Task Fails_when_the_model_does_not_produce_both_stems()
    {
        _runner.StemsFor = _ => ["(Vocals)"];

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Create().RunAsync(Source(), _work, Model(), dereverb: false, CancellationToken.None));
        Assert.Contains("instrumental", error.Message);
    }

    [Fact]
    public async Task Fails_instead_of_guessing_when_more_than_one_stem_is_unclear()
    {
        // Zwei unbekannte Namen lassen sich nicht mehr eindeutig zuordnen; ein falsch benannter
        // Stem wäre schlimmer als ein fehlgeschlagener Lauf.
        string[] stems = ["vocals", "drums", "bass", "other"];
        _runner.StemsFor = _ => ["(vocals)", "(drums)", "(low)", "(rest)"];

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Create().RunAsync(Source(), _work, Model("htdemucs.yaml", stems), dereverb: false, CancellationToken.None));
        Assert.Contains("bass", error.Message);
    }

    [Fact]
    public async Task Adds_dry_vocals_and_reverb_when_dereverb_is_requested()
    {
        var archive = await Create().RunAsync(Source(), _work, Model(), dereverb: true, CancellationToken.None);

        Assert.Equal(["instrumental.wav", "vocals.wav", "vocals_dry.wav", "vocals_reverb.wav"], Entries(archive));
        var dereverb = _runner.All("mlx-audio-separator").Last();
        Assert.Contains("(Vocals)", dereverb.Arguments[0]);
        Assert.True(dereverb.Has("--model_filename", "dereverb_roformer.ckpt"));
    }

    [Fact]
    public async Task Omits_the_reverb_stem_when_the_model_does_not_produce_one()
    {
        _runner.StemsFor = model => model.Contains("dereverb") ? ["(noreverb)"] : ["(Vocals)", "(Instrumental)"];

        var archive = await Create().RunAsync(Source(), _work, Model(), dereverb: true, CancellationToken.None);

        Assert.Equal(["instrumental.wav", "vocals.wav", "vocals_dry.wav"], Entries(archive));
    }

    [Fact]
    public async Task Fails_when_dereverb_produces_no_dry_vocals()
    {
        _runner.StemsFor = model => model.Contains("dereverb") ? ["(reverb)"] : ["(Vocals)", "(Instrumental)"];

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Create().RunAsync(Source(), _work, Model(), dereverb: true, CancellationToken.None));
    }

    [Fact]
    public async Task Passes_the_model_directory_when_one_is_configured()
    {
        await Create(modelDirectory: "/models").RunAsync(Source(), _work, Model(), dereverb: false, CancellationToken.None);

        Assert.True(_runner.Single("mlx-audio-separator").Has("--model_file_dir", "/models"));
    }

    [Fact]
    public async Task Omits_the_model_directory_when_none_is_configured()
    {
        await Create().RunAsync(Source(), _work, Model(), dereverb: false, CancellationToken.None);

        Assert.DoesNotContain("--model_file_dir", _runner.Single("mlx-audio-separator").Arguments);
    }

    [Fact]
    public async Task Runs_only_one_separation_at_a_time()
    {
        // Die Metal-GPU verträgt keinen zweiten Lauf, deshalb muss der zweite Aufruf warten.
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        _runner.Before = async executable =>
        {
            if (executable != "ffprobe") return;
            entered.TrySetResult();
            await release.Task;
        };
        var service = Create();

        var first = service.RunAsync(Source(), _work, Model(), dereverb: false, CancellationToken.None);
        await entered.Task;
        var secondWork = Path.Combine(_work, "second");
        Directory.CreateDirectory(secondWork);
        var second = service.RunAsync(Source(), secondWork, Model(), dereverb: false, CancellationToken.None);

        Assert.NotSame(second, await Task.WhenAny(second, Task.Delay(250)));
        release.SetResult();
        await first;
        await second;
    }
}
