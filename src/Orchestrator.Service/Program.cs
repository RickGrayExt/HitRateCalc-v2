using Contracts;
using MassTransit;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<HitRateCalculatedConsumer>();
    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host("rabbitmq", h => { });
        cfg.ConfigureEndpoints(context);
    });
});

var app = builder.Build();

app.MapGet("/health", () => "ok");

app.MapGet("/result/{runId:guid}", (Guid runId) =>
{
    if (Program.TryGetResult(runId, out var hitRate))
    {
        return Results.Json(new { runId, hitRate, status = "completed" });
    }
    return Results.Json(new { runId, status = "processing" });
});

app.Run("http://0.0.0.0:8080");

public class HitRateCalculatedConsumer : IConsumer<HitRateCalculated>
{
    private readonly ILogger<HitRateCalculatedConsumer> _logger;

    public HitRateCalculatedConsumer(ILogger<HitRateCalculatedConsumer> logger)
    {
        _logger = logger;
    }

    public Task Consume(ConsumeContext<HitRateCalculated> context)
    {
        _logger.LogInformation("Received hit rate calculation result: RunId={RunId}, HitRate={HitRate}", 
            context.Message.RunId, context.Message.HitRate);

        Program.StoreResult(context.Message.RunId, context.Message.HitRate);
        return Task.CompletedTask;
    }
}

public static partial class Program
{
    private static readonly Dictionary<Guid, double> _results = new();

    public static void StoreResult(Guid runId, double hitRate)
    {
        _results[runId] = hitRate;
    }

    public static bool TryGetResult(Guid runId, out double hitRate)
    {
        return _results.TryGetValue(runId, out hitRate);
    }
}