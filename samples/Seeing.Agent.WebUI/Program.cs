using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using AntDesign;
using Seeing.Agent;
using Seeing.Agent.Extensions;
using Seeing.Agent.WebUI.State;
using Seeing.Agent.WebUI.Services;
using Seeing.Agent.Core.Interfaces;
using Seeing.Session.Core;
using Seeing.Session.Management;
using Seeing.Session.Storage;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();

builder.Services.AddSeeingAgent(builder.Configuration);

// === Session 管理服务（统一使用 Seeing.Session）===
builder.Services.AddSingleton<ISessionStore, FileSessionStore>();
builder.Services.AddSingleton<SessionManager>();
builder.Services.AddSingleton<ISessionEventPublisher, SessionEventPublisher>();
builder.Services.AddSingleton<ISessionLifecycle, SessionLifecycle>();
builder.Services.AddScoped<SessionProvider>();

// === WebUI 服务 ===
builder.Services.AddScoped<IPermissionChannel, BlazorPermissionChannel>();
builder.Services.AddSingleton<AppState>();
builder.Services.AddScoped<SessionState>();
builder.Services.AddScoped<EventStreamHandler>();
builder.Services.AddScoped<ErrorHandlingService>();

// AntDesign 2.0 配置
builder.Services.AddAntDesign();

var app = builder.Build();

// 初始化 Seeing.Agent 组件（Skills/MCP/Plugins）
var workspaceRoot = Directory.GetCurrentDirectory();
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.InitializeSeeingAgentAsync(workspaceRoot);
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();