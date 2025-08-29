using Calculation.Service.Services;
using Calculation.Service.Models;
using Calculation.Service.Services;
using Contracts;
using CsvHelper;
using CsvHelper.TypeConversion;
using CsvHelper.Configuration;
using MassTransit;
using System.Globalization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<StartRunCommandConsumer>();
    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host("rabbitmq", h => { });
        cfg.ConfigureEndpoints(context);
    });
});

builder.Services.AddScoped<IHitRateCalculationService, HitRateCalculationService>();

var app = builder.Build();

app.MapGet("/health", () => "ok");

app.Run("http://0.0.0.0:8080");

public class StartRunCommandConsumer : IConsumer<StartRunCommand>
{
    private readonly IHitRateCalculationService _calculationService;
    private readonly IBus _bus;
    private readonly ILogger<StartRunCommandConsumer> _logger;

    public StartRunCommandConsumer(
        IHitRateCalculationService calculationService, 
        IBus bus, 
        ILogger<StartRunCommandConsumer> logger)
    {
        _calculationService = calculationService;
        _bus = bus;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<StartRunCommand> context)
    {
        try
        {
            _logger.LogInformation("Starting calculation for RunId: {RunId}", context.Message.RunId);

            var orders = LoadDataset(context.Message.DatasetPath);
            var hitRate = await _calculationService.CalculateHitRateAsync(
                orders, 
                context.Message.MaxOrdersPerStation,
                context.Message.NumberOfStations,
                context.Message.MaxSkusPerRack);

            await _bus.Publish(new HitRateCalculated
            {
                RunId = context.Message.RunId,
                HitRate = hitRate
            });

            _logger.LogInformation("Calculation completed for RunId: {RunId}, HitRate: {HitRate}", context.Message.RunId, hitRate);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calculating hit rate for RunId: {RunId}", context.Message.RunId);
            throw;
        }
    }

    private List<Order> LoadDataset(string datasetPath)
    {
        var orders = new List<Order>();

        using var reader = new StreamReader(datasetPath);
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
        
        csv.Context.RegisterClassMap<OrderMap>();
        orders = csv.GetRecords<Order>().ToList();

        _logger.LogInformation("Loaded {Count} orders from dataset", orders.Count);
        return orders;
    }
}

public class OrderMap : ClassMap<Order>
{
    public OrderMap()
    {
        Map(m => m.OrderDate).Name("Order_Date");
        Map(m => m.Time).Name("Time");
        Map(m => m.CustomerId).Name("Customer_Id");
        Map(m => m.ProductCategory).Name("Product_Category");
        Map(m => m.Product).Name("Product");
        Map(m => m.Sales).Name("Sales").TypeConverter<NullableDecimalConverter>();
        Map(m => m.Quantity).Name("Quantity").TypeConverter<NullableIntConverter>();
        Map(m => m.OrderPriority).Name("Order_Priority").Optional();
    }
}
public class NullableIntConverter : DefaultTypeConverter
{
    public override object ConvertFromString(string text, IReaderRow row, MemberMapData memberMapData)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 1; // Default to 1 for missing quantities (assuming at least 1 item)
        
        if (int.TryParse(text, out var result))
            return result;
        
        return 1; // Default to 1 if parsing fails
    }
}

public class NullableDecimalConverter : DefaultTypeConverter
{
    public override object ConvertFromString(string text, IReaderRow row, MemberMapData memberMapData)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0m; // Default to 0 for missing sales values
        
        if (decimal.TryParse(text, out var result))
            return result;
        
        return 0m; // Default to 0 if parsing fails
    }
}



