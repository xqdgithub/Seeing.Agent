using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Chat;
using Seeing.Agent.Agents.BuiltIn;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Extensions;
using Seeing.Agent.Hosting;
using Seeing.Agent.Hosting.Embed;
using Seeing.Agent.Llm.OpenAI;
using Seeing.Agent.Core.Tools.Basic;
using Seeing.IO.Local;

// Embed Host Shape 最小用法：
//   AddSeeingModule* → AddSeeingCore → AddSeeingEmbedHost（或 AddSeeingHostingEmbed）
//   → Build → InitializeSeeingAsync（Host.Start 之前）
// 不引用 Hosting.Web / Blazor；能力包由本 sample 自行点名引用。

var builder = Host.CreateApplicationBuilder(args);

var registry = new ConfigSectionRegistry();
builder.Services.AddSingleton<IConfigSectionRegistry>(registry);
builder.Services.AddSeeingModule<LocalExecutionWorldModule>(registry);
builder.Services.AddSeeingModule<BasicModule>(registry);
builder.Services.AddSeeingModule<OpenAiLlmModule>(registry);
builder.Services.AddSeeingModule<AgentsBuiltInModule>(registry);
builder.Services.AddSeeingCore(registry);
builder.Services.AddSeeingEmbedHost();

using var host = builder.Build();
await host.Services.InitializeSeeingAsync();

var shape = host.Services.GetRequiredService<HostShapeDescriptor>();
var orchestrator = host.Services.GetRequiredService<IChatOrchestrator>();

Console.WriteLine($"Embed host ready: shape={shape.Id}, scenario={shape.DefaultScenario}");
Console.WriteLine($"Carriable modules: {string.Join(", ", shape.CarriableModuleTypes)}");
Console.WriteLine($"IChatOrchestrator: {orchestrator.GetType().Name}");
