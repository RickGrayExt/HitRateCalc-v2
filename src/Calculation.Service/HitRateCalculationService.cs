using Calculation.Service.Models;

namespace Calculation.Service.Services
{
    public class HitRateCalculationService : IHitRateCalculationService
    {
        private readonly ILogger<HitRateCalculationService> _logger;

        public HitRateCalculationService(ILogger<HitRateCalculationService> logger)
        {
            _logger = logger;
        }

        public async Task<double> CalculateHitRateAsync(
            List<Order> orders, 
            int maxOrdersPerStation, 
            int numberOfStations, 
            int maxSkusPerRack)
        {
            return await CalculatePickToOrderHitRateAsync(orders, maxSkusPerRack);
        }

        public async Task<double> CalculatePickToOrderHitRateAsync(
            List<Order> orders, 
            int maxSkusPerRack)
        {
            _logger.LogInformation("Starting Pick-to-Order hit rate calculation with {OrderCount} orders", orders.Count);

            // Create order groups
            var orderGroups = CreateOrderGroups(orders);
            _logger.LogInformation("Created {GroupCount} order groups", orderGroups.Count);

            // Create waves
            var waves = CreateWaves(orderGroups);
            _logger.LogInformation("Created {WaveCount} waves", waves.Count);

            var allResults = new List<PickToOrderResult>();

            foreach (var wave in waves)
            {
                var waveResult = CalculatePickToOrderHitRateForWave(wave, maxSkusPerRack);
                allResults.Add(waveResult);
            }

            // Calculate overall hit rate across all waves
            var totalItemsPickedFromAllRacks = allResults.Sum(r => r.SumOfItemsPickedFromAllRacks);
            var totalItems = allResults.Sum(r => r.TotalItems);

            var overallHitRate = totalItems > 0 ? (double)totalItemsPickedFromAllRacks / totalItems : 0;
            
            _logger.LogInformation("Pick-to-Order calculation complete. Hit rate: {HitRate}", overallHitRate);
            return overallHitRate;
        }

        private PickToOrderResult CalculatePickToOrderHitRateForWave(Wave wave, int maxSkusPerRack)
        {
            var result = new PickToOrderResult();
            
            // T = Total items in the wave
            result.TotalItems = wave.TotalUnits;
            
            // Create racks based on unique SKUs in the wave
            var racks = CreateRacksForWave(wave, maxSkusPerRack);
            
            // For Pick-to-Order: each order is processed individually
            var rackPresentations = new List<RackPresentation>();
            int presentationId = 1;

            foreach (var orderGroup in wave.OrderGroups)
            {
                var requiredSkus = orderGroup.UniqueSkus;
                
                // Find racks that contain the required SKUs
                var relevantRacks = racks.Where(r => r.Skus.Any(sku => requiredSkus.Contains(sku))).ToList();
                
                foreach (var rack in relevantRacks)
                {
                    var presentation = new RackPresentation
                    {
                        RackId = rack.RackId,
                        PresentationId = presentationId++,
                        AvailableSkus = rack.Skus.ToList()
                    };

                    // Calculate items picked from this rack for this order
                    var itemsFromThisRack = 0;
                    foreach (var orderLine in orderGroup.OrderLines)
                    {
                        if (rack.Skus.Contains(orderLine.Sku))
                        {
                            itemsFromThisRack += orderLine.Quantity;
                            presentation.PickedItems.Add(new PickedItem
                            {
                                Sku = orderLine.Sku,
                                Quantity = orderLine.Quantity,
                                OrderId = orderGroup.OrderId
                            });
                        }
                    }

                    presentation.ItemsPickedFromThisRack = itemsFromThisRack;
                    
                    // Only add presentation if items were actually picked
                    if (itemsFromThisRack > 0)
                    {
                        rackPresentations.Add(presentation);
                    }
                }
            }

            result.RackPresentations = rackPresentations;
            result.TotalRackPresentations = rackPresentations.Count; // R in formula
            result.SumOfItemsPickedFromAllRacks = rackPresentations.Sum(rp => rp.ItemsPickedFromThisRack); // Σ(I_r_i)
            
            // Apply the exact formula: HR = Σ(I_r_i) / T
            result.HitRate = result.TotalItems > 0 
                ? (double)result.SumOfItemsPickedFromAllRacks / result.TotalItems 
                : 0;

            return result;
        }

        private List<Rack> CreateRacksForWave(Wave wave, int maxSkusPerRack)
        {
            var allSkus = wave.OrderGroups
                .SelectMany(og => og.UniqueSkus)
                .Distinct()
                .ToList();

            var racks = new List<Rack>();
            var rackId = 1;

            // Simple rack assignment: group SKUs sequentially
            for (int i = 0; i < allSkus.Count; i += maxSkusPerRack)
            {
                var rackSkus = allSkus.Skip(i).Take(maxSkusPerRack).ToList();
                racks.Add(new Rack
                {
                    RackId = rackId++,
                    Skus = rackSkus,
                    MaxSkus = maxSkusPerRack
                });
            }

            return racks;
        }

        private List<OrderGroup> CreateOrderGroups(List<Order> orders)
        {
            return orders
                .GroupBy(o => o.GetOrderId())
                .Select(g => new OrderGroup
                {
                    OrderId = g.Key,
                    CustomerId = g.First().CustomerId,
                    OrderDateTime = g.First().GetOrderDateTime(),
                    OrderLines = g.Select(o => new OrderLine
                    {
                        Sku = o.Product,
                        ProductCategory = o.ProductCategory,
                        Quantity = o.Quantity,
                        Sales = o.Sales
                    }).ToList()
                })
                .OrderBy(og => og.OrderDateTime)
                .ToList();
        }

        private List<Wave> CreateWaves(List<OrderGroup> orderGroups)
        {
            var waves = new List<Wave>();
            var currentWave = new Wave();
            
            DateTime? currentHour = null;

            foreach (var orderGroup in orderGroups)
            {
                var orderHour = new DateTime(
                    orderGroup.OrderDateTime.Year,
                    orderGroup.OrderDateTime.Month,
                    orderGroup.OrderDateTime.Day,
                    orderGroup.OrderDateTime.Hour,
                    0, 0);

                if (currentHour == null || currentHour != orderHour)
                {
                    if (currentWave.OrderGroups.Any())
                    {
                        waves.Add(currentWave);
                    }
                    
                    currentWave = new Wave
                    {
                        StartTime = orderHour,
                        EndTime = orderHour.AddHours(1)
                    };
                    currentHour = orderHour;
                }

                currentWave.OrderGroups.Add(orderGroup);
            }

            if (currentWave.OrderGroups.Any())
            {
                waves.Add(currentWave);
            }

            return waves;
        }
    }
}