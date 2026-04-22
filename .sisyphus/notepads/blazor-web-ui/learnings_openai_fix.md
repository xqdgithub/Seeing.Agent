OpenAI SDK 2.0.0 compatibility fix

- Context: OpenAiClient.cs referenced ChatInputAudioFormat which does not exist in OpenAI SDK 2.0.0, causing CS0246.
- Change: Replaced usage of ChatInputAudioFormat with the mime-type string provided by the user when constructing input_audio content.
- Reasoning: SDK 2.0.0 expects CreateInputAudioPart(data, mimeType) rather than an enum type; this aligns with the 2.0.0 API surface.
- Verification steps: dotnet build src/Seeing.Agent should succeed with 0 errors after applying changes. Also ensure the OpenAI audio input path is exercised in tests if present.

Files touched:
- src/Seeing.Agent/Llm/Clients/OpenAiClient.cs
