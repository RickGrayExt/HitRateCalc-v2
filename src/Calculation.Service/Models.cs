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
    }

    public class RackPresentation
    {
        public int RackId { get; set; }
        public int StationId { get; set; }
        public List<string> AvailableSkus { get; set; } = new();
        public int UnitsPicked { get; set; }
        public List<OrderGroup> FulfilledOrders { get; set; } = new();
    }

    public class HitRateResult
    {
        public double HitRate { get; set; }
        public int TotalUnits { get; set; }
        public int TotalRackPresentations { get; set; }
        public int TotalWaves { get; set; }
        public int TotalOrders { get; set; }
        public List<WaveResult> WaveResults { get; set; } = new();
    }

    public class WaveResult
    {
        public int WaveNumber { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public int UnitsPickedInWave { get; set; }
        public int RackPresentationsInWave { get; set; }
        public double WaveHitRate { get; set; }
    }
}