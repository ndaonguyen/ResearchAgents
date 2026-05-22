using System.Text.Json;
using AgentScope.Application;
using AgentScope.Application.Abstractions;
using AgentScope.Application.Runs;
using AgentScope.Evals;
using AgentScope.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Eval harness entry point.
//
// Usage:
//   dotnet run --project tests/AgentScope.Evals -- \
//     --variant baseline \
//     --questions questions/sample.json \
//     --out results/baseline.jsonl
//
//   dotnet run --project tests/AgentScope.Evals -- \
//     --variant haiku-researchers \
//     --questions questions/sample.json \
//     --researcher-model gpt-4o-mini \
//     --synthesizer-model gpt-4o
//
// Variants are compared by running the harness multiple times, once per --variant,
// then comparing the JSONL outputs.

var parsed = CliArgs.Parse(args);
if (parsed is null)
{
    PrintUsage();
    return 1;
}

// Console apps read appsettings.json from CWD by default. That breaks when you launch
// from the repo root (`dotnet run --project ...`) — the bin-dir appsettings is invisible.
// Pin the content root to where the binaries live, and default to Development so
// appsettings.Development.json + user secrets load automatically.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    EnvironmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? Environments.Development
});

// Quiet noisy SK logs by default so the CLI output stays readable. Override via env var.
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
builder.Logging.AddFilter("Microsoft.SemanticKernel", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);

builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration);

// Eager preflight: a missing API key fails deep inside the orchestrator with a generic
// ArgumentException. Surface it once, up front, with actionable guidance instead.
var openAiKey = builder.Configuration["AgentScope:OpenAi:ApiKey"];
if (string.IsNullOrWhiteSpace(openAiKey) || openAiKey == "set-via-user-secrets-or-environment")
{
    Console.Error.WriteLine("""
        ERROR: AgentScope:OpenAi:ApiKey is not configured for the eval CLI.

        Three ways to set it (pick one):

          1. User secrets (recommended for local dev):
               dotnet user-secrets set "AgentScope:OpenAi:ApiKey" "sk-..." --project tests/AgentScope.Evals
               dotnet user-secrets set "AgentScope:Tavily:ApiKey" "tvly-..." --project tests/AgentScope.Evals

          2. Environment variables (good for CI):
               $env:AgentScope__OpenAi__ApiKey = "sk-..."
               $env:AgentScope__Tavily__ApiKey = "tvly-..."

          3. Copy your existing dev config:
               copy src\AgentScope.Web\appsettings.Development.json tests\AgentScope.Evals\appsettings.Development.json
        """);
    return 1;
}

builder.Services.AddSingleton<LlmJudge>();

using var host = builder.Build();

// Each question's run is independent but stateless agents can share the scope.
using var scope = host.Services.CreateScope();
var sp = scope.ServiceProvider;

var questions = LoadQuestions(parsed.QuestionsPath);
if (questions.Count == 0)
{
    Console.Error.WriteLine($"No questions found in {parsed.QuestionsPath}.");
    return 1;
}

var outPath = parsed.OutPath ?? DefaultOutPath(parsed.Variant);
await using var writer = new ResultsWriter(outPath);

var config = new OrchestratorConfig(
    PlannerModel:     parsed.PlannerModel,
    ResearcherModel:  parsed.ResearcherModel,
    CriticModel:      parsed.CriticModel,
    SynthesizerModel: parsed.SynthesizerModel,
    EnableCriticRetry: !parsed.NoRetry);

var runner = new EvalRunner(
    sp.GetRequiredService<StartRunUseCase>(),
    sp.GetRequiredService<LlmJudge>(),
    writer,
    sp.GetRequiredService<ILogger<EvalRunner>>());

Console.WriteLine($"Writing results to {writer.FilePath}");
Console.WriteLine();

using var stopCts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    Console.WriteLine("\nCancelling...");
    stopCts.Cancel();
    e.Cancel = true;
};

try
{
    await runner.RunVariantAsync(new EvalVariant(parsed.Variant, config), questions, stopCts.Token);
    return 0;
}
catch (OperationCanceledException)
{
    Console.WriteLine("Cancelled.");
    return 130;
}

static List<EvalQuestion> LoadQuestions(string path)
{
    var json = File.ReadAllText(path);
    return JsonSerializer.Deserialize<List<EvalQuestion>>(json, new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    }) ?? new();
}

static string DefaultOutPath(string variant)
{
    var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
    return Path.Combine("results", $"{variant}-{stamp}.jsonl");
}

static void PrintUsage()
{
    Console.Error.WriteLine("""
        Usage: dotnet run --project tests/AgentScope.Evals -- [options]

        Options:
          --variant <label>           Variant label (default: "baseline")
          --questions <path>          Path to question set JSON (default: "questions/sample.json")
          --out <path>                Output JSONL path (default: "results/<variant>-<timestamp>.jsonl")
          --planner-model <id>        Override planner model
          --researcher-model <id>     Override researcher model
          --critic-model <id>         Override critic model
          --synthesizer-model <id>    Override synthesizer model
          --no-retry                  Disable critic-driven retry
        """);
}

internal sealed record CliArgs(
    string Variant,
    string QuestionsPath,
    string? OutPath,
    string? PlannerModel,
    string? ResearcherModel,
    string? CriticModel,
    string? SynthesizerModel,
    bool NoRetry)
{
    public static CliArgs? Parse(string[] args)
    {
        string variant = "baseline";
        string questionsPath = "questions/sample.json";
        string? outPath = null;
        string? plannerModel = null;
        string? researcherModel = null;
        string? criticModel = null;
        string? synthesizerModel = null;
        bool noRetry = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--variant":           variant          = NextOrFail(args, ref i); break;
                case "--questions":         questionsPath    = NextOrFail(args, ref i); break;
                case "--out":               outPath          = NextOrFail(args, ref i); break;
                case "--planner-model":     plannerModel     = NextOrFail(args, ref i); break;
                case "--researcher-model":  researcherModel  = NextOrFail(args, ref i); break;
                case "--critic-model":      criticModel      = NextOrFail(args, ref i); break;
                case "--synthesizer-model": synthesizerModel = NextOrFail(args, ref i); break;
                case "--no-retry":          noRetry          = true; break;
                case "--help" or "-h":      return null;
                default:
                    // Ignore unknown args silently — Microsoft.Extensions.Hosting injects its
                    // own (e.g. environment) that we don't want to choke on.
                    break;
            }
        }

        return new CliArgs(variant, questionsPath, outPath, plannerModel, researcherModel, criticModel, synthesizerModel, noRetry);
    }

    private static string NextOrFail(string[] args, ref int i)
    {
        if (i + 1 >= args.Length) throw new ArgumentException($"Missing value for {args[i]}");
        return args[++i];
    }
}
