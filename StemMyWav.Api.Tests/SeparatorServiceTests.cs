using System.IO.Compression;
using Microsoft.Extensions.Options;
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
            Model = "bs_roformer.ckpt",
            DereverbModel = "dereverb_roformer.ckpt",
            ModelDirectory = modelDirectory
        }));

    private string Source()
    {
        var source = Path.Combine(_work, "input.flac");
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
        var archive = await Create().RunAsync(Source(), _work, dereverb: false, CancellationToken.None);

        Assert.Equal(["instrumental.wav", "vocals.wav"], Entries(archive));
        foreach (var conversion in _runner.All("ffmpeg"))
            Assert.True(conversion.Has("-ac", "2", "-ar", "48000", "-c:a", "pcm_s16le"),
                "Jeder Stem muss auf 48 kHz, 16 Bit und Stereo konvertiert werden.");
    }

    [Fact]
    public async Task Prepares_the_input_when_it_is_not_stereo_at_forty_four_one()
    {
        _runner.ProbeResult = FakeProcessRunner.Probe("flac", "48000", 1);

        await Create().RunAsync(Source(), _work, dereverb: false, CancellationToken.None);

        var prepare = _runner.All("ffmpeg").First();
        Assert.True(prepare.Has("-ac", "2", "-ar", "44100", "-c:a", "flac"));
        Assert.EndsWith("prepared.flac", prepare.Arguments[^1]);
        Assert.Equal(Path.Combine(_work, "prepared.flac"), _runner.Single("mlx-audio-separator").Arguments[0]);
    }

    [Fact]
    public async Task Passes_the_original_input_when_it_already_matches()
    {
        var source = Source();

        await Create().RunAsync(source, _work, dereverb: false, CancellationToken.None);

        Assert.DoesNotContain(_runner.All("ffmpeg"), call => call.Has("-ar", "44100"));
        Assert.Equal(source, _runner.Single("mlx-audio-separator").Arguments[0]);
    }

    [Fact]
    public async Task Rejects_input_that_is_not_flac()
    {
        _runner.ProbeResult = FakeProcessRunner.Probe("mp3", "44100", 2);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Create().RunAsync(Source(), _work, dereverb: false, CancellationToken.None));
    }

    [Fact]
    public async Task Fails_when_the_model_does_not_produce_both_stems()
    {
        _runner.StemsFor = _ => ["(Vocals)"];

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Create().RunAsync(Source(), _work, dereverb: false, CancellationToken.None));
        Assert.Contains("vocals and instrumental", error.Message);
    }

    [Fact]
    public async Task Adds_dry_vocals_and_reverb_when_dereverb_is_requested()
    {
        var archive = await Create().RunAsync(Source(), _work, dereverb: true, CancellationToken.None);

        Assert.Equal(["instrumental.wav", "vocals.wav", "vocals_dry.wav", "vocals_reverb.wav"], Entries(archive));
        var dereverb = _runner.All("mlx-audio-separator").Last();
        Assert.Contains("(Vocals)", dereverb.Arguments[0]);
        Assert.True(dereverb.Has("--model_filename", "dereverb_roformer.ckpt"));
    }

    [Fact]
    public async Task Omits_the_reverb_stem_when_the_model_does_not_produce_one()
    {
        _runner.StemsFor = model => model.Contains("dereverb") ? ["(noreverb)"] : ["(Vocals)", "(Instrumental)"];

        var archive = await Create().RunAsync(Source(), _work, dereverb: true, CancellationToken.None);

        Assert.Equal(["instrumental.wav", "vocals.wav", "vocals_dry.wav"], Entries(archive));
    }

    [Fact]
    public async Task Fails_when_dereverb_produces_no_dry_vocals()
    {
        _runner.StemsFor = model => model.Contains("dereverb") ? ["(reverb)"] : ["(Vocals)", "(Instrumental)"];

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Create().RunAsync(Source(), _work, dereverb: true, CancellationToken.None));
    }

    [Fact]
    public async Task Passes_the_model_directory_when_one_is_configured()
    {
        await Create(modelDirectory: "/models").RunAsync(Source(), _work, dereverb: false, CancellationToken.None);

        Assert.True(_runner.Single("mlx-audio-separator").Has("--model_file_dir", "/models"));
    }

    [Fact]
    public async Task Omits_the_model_directory_when_none_is_configured()
    {
        await Create().RunAsync(Source(), _work, dereverb: false, CancellationToken.None);

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

        var first = service.RunAsync(Source(), _work, dereverb: false, CancellationToken.None);
        await entered.Task;
        var secondWork = Path.Combine(_work, "second");
        Directory.CreateDirectory(secondWork);
        var second = service.RunAsync(Source(), secondWork, dereverb: false, CancellationToken.None);

        Assert.NotSame(second, await Task.WhenAny(second, Task.Delay(250)));
        release.SetResult();
        await first;
        await second;
    }
}
