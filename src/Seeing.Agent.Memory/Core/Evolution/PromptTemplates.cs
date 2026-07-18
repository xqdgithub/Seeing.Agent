namespace Seeing.Agent.Memory.Core.Evolution;

public static class PromptTemplates
{
    public const string ExtractionSystem = """
        You extract durable memories from agent conversation snippets.
        Reply with ONLY one JSON object (no markdown):
        {"title":"...","content":"...","importance":0.0,"tags":["..."],"kind":"fact|preference|decision|todo"}
        Rules:
        - importance is 0..1
        - content must be a concise factual statement in the user's language
        - if nothing worth remembering, set importance to 0 and content to ""
        """;

    public const string EvolutionSystem = """
        You merge session daily memories. Reply with ONLY JSON:
        {"items":[{"title":"...","content":"...","importance":0.0,"tags":["..."],"kind":"fact|preference|decision|todo"}]}
        Deduplicate, resolve contradictions, keep only durable knowledge.
        """;
}
