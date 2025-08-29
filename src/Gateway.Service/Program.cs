using Contracts;
using MassTransit;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMassTransit(x =>
{
    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host("rabbitmq", h => { });
    });
});

var app = builder.Build();
app.MapGet("/health", () => "ok");

app.MapPost("/run", async (StartRunCommand req, IBus bus) =>
{
    var dataset = string.IsNullOrWhiteSpace(req.DatasetPath) ? "/app/data/DataSetClean.csv" : req.DatasetPath;
    var runId = req.RunId == Guid.Empty ? Guid.NewGuid() : req.RunId;
    
    var cmd = req with { RunId = runId, DatasetPath = dataset };
    await bus.Publish(cmd);

    return Results.Json(new { runId });
});

app.Run("http://0.0.0.0:8080");