namespace StemMyWav.Api.Separation;

/// <summary>Führt ein externes Programm aus. Die Trennung von der Ablauflogik macht
/// <see cref="SeparatorService"/> ohne FFmpeg und ohne Modelle prüfbar.</summary>
public interface IProcessRunner
{
    /// <summary>Startet das Programm und liefert dessen Standardausgabe.</summary>
    /// <exception cref="InvalidOperationException">Der Prozess endete mit einem Fehlercode.</exception>
    Task<string> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token);
}
