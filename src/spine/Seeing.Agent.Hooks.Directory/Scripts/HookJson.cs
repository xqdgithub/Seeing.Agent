using System.Text.Encodings.Web;
using System.Text.Json;

namespace Seeing.Agent.Hooks.Directory.Scripts;

internal static class HookJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
