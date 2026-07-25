using APIContagem;
using APIContagem.Data;
using APIContagem.Models;
using APIContagem.Tracing;
using APIContagem.Utils;
using DotNet.Testcontainers.Builders;
using Grafana.OpenTelemetry;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;
using Testcontainers.PostgreSql;

var builder = WebApplication.CreateBuilder(args);


Console.WriteLine("Criando container para uso do PostgreSQL...");
var postgresContainer = new PostgreSqlBuilder("postgres:17.6")
    .WithResourceMapping(
        DBFileAsByteArray.GetContent("BaseContagemPostgreSql.sql"),
        "/docker-entrypoint-initdb.d/01-init.sql")
    .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("pg_isready"))
    .Build();
await postgresContainer.StartAsync();

builder.Services.AddDbContext<ContagemPostgresContext>(options =>
{
    options.UseNpgsql(
        postgresContainer.GetConnectionString().Replace(";Database=postgres;",";Database=basecontagem;"),
        o => o.UseNodaTime());
});

var resourceBuilder = ResourceBuilder.CreateDefault()
    .AddService(serviceName: OpenTelemetryExtensions.ServiceName,
        serviceVersion: OpenTelemetryExtensions.ServiceVersion);
builder.Services.AddOpenTelemetry()
    .WithTracing((traceBuilder) =>
    {
        traceBuilder
            .AddSource(OpenTelemetryExtensions.ServiceName)
            .SetResourceBuilder(resourceBuilder)
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddEntityFrameworkCoreInstrumentation()
            .UseGrafana();
    });
builder.Logging.AddOpenTelemetry(options =>
{
    options.SetResourceBuilder(resourceBuilder);
    options.IncludeFormattedMessage = true;
    options.IncludeScopes = true;
    options.ParseStateValues = true;
    options.AttachLogsToActivityEvent();
    options.UseGrafana();
});
builder.Services.AddOpenTelemetry()
    .WithMetrics((metricBuilder) =>
    {
        metricBuilder.AddView(
            "http.server.request.duration",
            new ExplicitBucketHistogramConfiguration()
            {
                Boundaries = [0, 0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10]
            }
        );
        metricBuilder.AddMeter(
            "System.Diagnostics.Metrics",
            "Microsoft.AspNetCore.Hosting",
            "Microsoft.AspNetCore.Server.Kestrel",
            "System.Net.Http");
        metricBuilder
            .SetResourceBuilder(resourceBuilder)
            .AddAspNetCoreInstrumentation()
            .AddRuntimeInstrumentation()
            .AddProcessInstrumentation()
            .AddHttpClientInstrumentation()
            .AddConsoleExporter()
            .AddPrometheusExporter(options =>
            {
                options.ScrapeResponseCacheDurationMilliseconds = 0;
            })
            .UseGrafana();
    });

builder.Services.AddOpenApi();
builder.Services.AddCors();

builder.Services.AddScoped<ContagemRepository>();
builder.Services.AddSingleton<Contador>();

var app = builder.Build();

app.UseOpenTelemetryPrometheusScrapingEndpoint();

app.MapOpenApi();
app.MapScalarApiReference(options =>
{
    options.Title = "API de Contagem de Acessos";
    options.Theme = ScalarTheme.BluePlanet;
    options.DarkMode = true;
});

Lock ContagemLock = new();

app.MapGet("/contador", async (ContagemRepository repository, Contador contador) =>
{
    using var activity1 = OpenTelemetryExtensions.ActivitySource
        .StartActivity("GerarValorContagem")!;
            
    int valorAtualContador;
    using (ContagemLock.EnterScope())
    {
        contador.Incrementar();
        valorAtualContador = contador.ValorAtual;
    }
    activity1.SetTag("valorAtual", valorAtualContador);
    app.Logger.LogInformation($"Contador - Valor atual: {valorAtualContador}");

    
    var resultadoContador = new ResultadoContador()
    {
        ValorAtual = contador.ValorAtual,
        Local = contador.Local,
        Kernel = contador.Kernel,
        Framework = contador.Framework,
        Mensagem = app.Configuration["Saudacao"]
    };
    activity1.Stop();

    using var activity2 = OpenTelemetryExtensions.ActivitySource
        .StartActivity("RegistrarRetornarValorContagem")!;

    repository.Insert(resultadoContador);
    app.Logger.LogInformation($"Registro inserido com sucesso! Valor: {valorAtualContador}");

    activity2.SetTag("valorAtual", valorAtualContador);
    activity2.SetTag("horario", $"{DateTime.UtcNow.AddHours(-3):HH:mm:ss}");

    return Results.Ok(resultadoContador);
})
.Produces<ResultadoContador>();

app.MapGet("/connectionstring", () =>
{
    using var activity = OpenTelemetryExtensions.ActivitySource
        .StartActivity("ObterConnectionString")!;
    var connectionString = postgresContainer.GetConnectionString();
    app.Logger.LogInformation($"Connection string do PostgreSQL: {connectionString}");
    return Results.Text(connectionString, "application/text");
});

app.MapGet("/badrequest", () =>
{
    using var activity = OpenTelemetryExtensions.ActivitySource
        .StartActivity("SimularBadRequest")!;

    activity.SetTag("erro", "Simulação de Bad Request");
    app.Logger.LogWarning("Simulação de Bad Request realizada.");

    return Results.BadRequest(new { Erro = "Este é um Bad Request simulado." });
});

app.MapGet("/error", () =>
{
    using var activity = OpenTelemetryExtensions.ActivitySource
        .StartActivity("SimularErroInterno")!;

    activity.SetTag("erro", "Simulação de erro interno");
    app.Logger.LogError("Simulação de erro interno (500).");

    throw new InvalidOperationException("Erro simulado para teste de métricas");
});

app.Run();