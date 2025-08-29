using System.Globalization;

namespace Calculation.Service.Models
{
    public class Order
    {
        public string OrderDate { get; set; } = string.Empty;
        public string Time { get; set; } = string.Empty;
        public string CustomerId { get; set; } = string.Empty;
        public string ProductCategory { get; set; } = string.Empty;
        public string Product { get; set; } = string.Empty;
        public decimal Sales { get; set; }
        public int Quantity { get; set; }
        public string? OrderPriority { get; set; }

        public DateTime GetOrderDateTime()
        {
            var dateStr = $"{OrderDate} {Time}";
            if (DateTime.TryParseExact(dateStr, "dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var result))
            {
                return result;
            }
            
            // Try alternative formats
            if (DateTime.TryParseExact(dateStr, "dd/MM/yyyy H:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out result))
            {
                return result;
            }
            
            throw new FormatException($"Unable to parse date time: {dateStr}");
        }

        public string GetOrderId()
        {
            return $"{CustomerId}_{GetOrderDateTime():yyyyMMdd_HHmmss}";
        }
    }

    public class Wave
    {
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public List<OrderGroup> OrderGroups { get; set; } = new();
        
        public int TotalUnits => OrderGroups.Sum(og => og.TotalQuantity);
        public int TotalOrders => OrderGroups.Count;
    }

    public class OrderGroup
    {
        public string OrderId { get; set; } = string.Empty;
        public string CustomerId { get; set; } = string.Empty;
        public DateTime OrderDateTime { get; set; }
        public List<OrderLine> OrderLines { get; set; } = new();
        
        public int TotalQuantity => OrderLines.Sum(ol => ol.Quantity);
        public List<string> UniqueSkus => OrderLines.Select(ol => ol.Sku).Distinct().ToList();
    }

    public class OrderLine
    {
        public string Sku { get; set; } = string.Empty;
        public string ProductCategory { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal Sales { get; set; }
    }

    public class Rack
    {
        public int RackId { get; set; }
        public List<string> Skus { get; set; } = new();
        public int MaxSkus { get; set; }
        
        public bool CanAddSku => Skus.Count < MaxSkus;
    }

    public class Station
    {
        public int StationId { get; set; }
        public List<OrderGroup> AssignedOrders { get; set; } = new();
        public int MaxOrders { get; set; }
        
        public bool CanTakeOrder => AssignedOrders.Count < MaxOrders;
        public List<string> RequiredSkus => AssignedOrders
            .SelectMany(o => o.UniqueSkus)
            .Distinct()
            .ToList();
        
        // Add StationResults property that was missing
        public List<RackPresentation> StationResults { get; set; } = new();
    }

    public class RackPresentation
    {
        public int RackId { get; set; }
        public int StationId { get; set; }
        public List<string> AvailableSkus { get; set; } = new();
        public int UnitsPicked { get; set; }
        public List<OrderGroup> FulfilledOrders { get; set; } = new();
        
        // Add additional properties that might be needed
        public DateTime PresentationTime { get; set; } = DateTime.UtcNow;
        public bool IsCompleted { get; set; }
    }

    public class HitRateResult
    {
        public double HitRate { get; set; }
        public int TotalUnits { get; set; }
        public int TotalRackPresentations { get; set; }
        public int TotalWaves { get; set; }
        public int TotalOrders { get; set; }
        public List<WaveResult> WaveResults { get; set; } = new();
        
        // Add calculation metadata
        public DateTime CalculationDateTime { get; set; } = DateTime.UtcNow;
        public TimeSpan CalculationDuration { get; set; }
    }

    public class WaveResult
    {
        public int WaveNumber { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public int UnitsPickedInWave { get; set; }
        public int RackPresentationsInWave { get; set; }
        public double WaveHitRate { get; set; }
        
        // Add station results for the wave
        public List<StationResult> StationResults { get; set; } = new();
        public int TotalOrdersInWave { get; set; }
        public List<string> UniqueSkusInWave { get; set; } = new();
    }

    // Add StationResult class that was referenced but missing
    public class StationResult
    {
        public int StationId { get; set; }
        public List<RackPresentation> RackPresentations { get; set; } = new();
        public int TotalUnitsPicked { get; set; }
        public int TotalRackPresentations { get; set; }
        public double StationHitRate { get; set; }
        public List<OrderGroup> ProcessedOrders { get; set; } = new();
        public TimeSpan ProcessingTime { get; set; }
    }

    // Add calculation summary class
    public class CalculationSummary
    {
        public Guid RunId { get; set; }
        public double OverallHitRate { get; set; }
        public int TotalOrders { get; set; }
        public int TotalUnits { get; set; }
        public int TotalRackPresentations { get; set; }
        public int TotalWaves { get; set; }
        public int TotalStations { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan TotalProcessingTime { get; set; }
        public List<WaveResult> WaveBreakdown { get; set; } = new();
    }
}