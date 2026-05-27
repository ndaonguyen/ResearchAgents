using AgentScope.Application;
using AgentScope.Infrastructure.DependencyInjection;
using AgentScope.Web.Components;
using AgentScope.Web.Hubs;
using AgentScope.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSignalR();

builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration);

builder.Services.AddScoped<EvalResultsReader>();
builder.Services.AddSingleton<RunPersister>();

// Eval queue + worker — singleton queue, BackgroundService that drains it.
builder.Services.AddSingleton<EvalQueue>();
builder.Services.AddHostedService<EvalWorker>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
   .AddInteractiveServerRenderMode();

app.MapHub<AgentHub>("/hubs/agents");

app.Run();
