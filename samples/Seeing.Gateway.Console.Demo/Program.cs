using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seeing.Gateway.Client;
using Seeing.Gateway.Client.Extensions;
using Seeing.Gateway.Models;

namespace Seeing.Gateway.Console.Demo;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var sessionId = "console-demo";
        var transport = GatewayClientTransport.HttpSse;
        var positional = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--transport" && i + 1 < args.Length)
            {
                transport = args[++i].Equals("ws", StringComparison.OrdinalIgnoreCase)
                    ? GatewayClientTransport.WebSocket
                    : GatewayClientTransport.HttpSse;
            }
            else
            {
                positional.Add(args[i]);
            }
        }

        if (positional.Count > 0)
            sessionId = positional[0];

        using var host = Host.CreateDefaultBuilder()
            .ConfigureServices((context, services) =>
            {
                services.Configure<GatewayClientOptions>(context.Configuration.GetSection(GatewayClientOptions.SectionName));
                services.PostConfigure<GatewayClientOptions>(options => options.Transport = transport);
                services.AddSeeingGatewayClient();
            })
            .ConfigureLogging(logging =>
            {
                logging.AddConsole();
                logging.SetMinimumLevel(LogLevel.Information);
            })
            .Build();

        var client = host.Services.GetRequiredService<IGatewayClient>();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("GatewayDemo");

        global::System.Console.WriteLine("Seeing Gateway Console Demo");
        global::System.Console.WriteLine($"SessionId: {sessionId}");
        global::System.Console.WriteLine($"Transport: {transport}");
        global::System.Console.WriteLine("输入消息并回车发送，输入 exit 退出。");
        global::System.Console.WriteLine();

        while (true)
        {
            global::System.Console.Write("> ");
            var input = global::System.Console.ReadLine();
            if (input is null || string.Equals(input, "exit", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(input))
            {
                continue;
            }

            var request = new GatewayRequest
            {
                SessionId = sessionId,
                Input = [new GatewayTextContentPart(input)],
                Stream = true
            };

            try
            {
                var submit = await client.SubmitAsync(request);
                if (!submit.Success || string.IsNullOrEmpty(submit.ExecutionId))
                {
                    global::System.Console.WriteLine($"[error] {submit.Error ?? "Submit failed"}");
                    continue;
                }

                await foreach (var gatewayEvent in client.SubscribeAsync(sessionId, submit.ExecutionId))
                {
                    PrintEvent(gatewayEvent);
                }

                global::System.Console.WriteLine();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Chat request failed");
                global::System.Console.WriteLine($"[error] {ex.Message}");
            }
        }

        if (client is IAsyncDisposable disposable)
            await disposable.DisposeAsync();

        return 0;
    }

    private static void PrintEvent(GatewayEvent gatewayEvent)
    {
        if (gatewayEvent.Object == GatewayEventObject.Content
            && gatewayEvent.Data?.Delta == true
            && !string.IsNullOrEmpty(gatewayEvent.Data.Text))
        {
            global::System.Console.Write(gatewayEvent.Data.Text);
            return;
        }

        var json = JsonSerializer.Serialize(gatewayEvent, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        });
        global::System.Console.WriteLine($"[{gatewayEvent.Object}/{gatewayEvent.Status}] {json}");
    }
}
