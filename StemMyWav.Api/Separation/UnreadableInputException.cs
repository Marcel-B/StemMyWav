namespace StemMyWav.Api.Separation;

/// <summary>Die hochgeladene Datei lässt sich nicht lesen. Das ist ein Fehler des Aufrufers,
/// kein Serverfehler — der Unterschied entscheidet darüber, ob der Gateway den Auftrag
/// wiederholt oder ihn sofort als fehlgeschlagen abschließt.</summary>
public sealed class UnreadableInputException(string message, Exception? inner = null)
    : Exception(message, inner);
