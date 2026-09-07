using MtgEngine.Cards.Pipeline.Translate;

namespace MtgEngine.Cards.Pipeline;

/// <summary>
/// What is left of the wave-0 translator. Everything it did is now the translation stage's:
/// the prompt and the parser are <see cref="TranslationPrompt"/>, the batch calls are
/// <see cref="AnthropicTranslationClient"/>, the resumable record is
/// <see cref="TranslationStore"/>, and the repair round is <see cref="TranslateStage"/>. Two
/// copies of a prompt drift and then disagree, which is why the copy here is gone
/// (seam-requests/DECISIONS.md, P1).
///
/// This forwarder exists only because <c>tests/MtgEngine.Pipeline.Tests/SmokeTests.cs</c> still
/// calls <c>Translator.Parse</c> and belongs to another agent; the seam request in
/// <c>seam-requests/P5.md</c> asks for that one line to move to
/// <see cref="TranslationPrompt.Parse"/>, and this file goes with it.
/// </summary>
public static class Translator
{
    /// <inheritdoc cref="TranslationPrompt.Parse"/>
    public static (CardDefinition? Definition, string? Error) Parse(string text) => TranslationPrompt.Parse(text);
}
