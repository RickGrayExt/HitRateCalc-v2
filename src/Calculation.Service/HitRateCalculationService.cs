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
            
            // Track which orders have been fully processed
            var remainingOrders = wave.OrderGroups.ToList();
            var rackPresentations = new List<RackPresentation>();
            int presentationId = 1;

            // Continue until all orders are fulfilled
            while (remainingOrders.Any())
            {
                // Find the rack that can fulfill the most items from remaining orders
                var bestRackForRound = FindBestRackForCurrentOrders(racks, remainingOrders);
                
                if (bestRackForRound.rack == null)
                {
                    // No more racks can fulfill any remaining orders - shouldn't happen in normal operation
                    _logger.LogWarning("No rack found to fulfill remaining orders");
                    break;
                }

                var presentation = new RackPresentation
                {
                    RackId = bestRackForRound.rack.RackId,
                    PresentationId = presentationId++,
                    AvailableSkus = bestRackForRound.rack.Skus.ToList()
                };

                // Process each remaining order against this rack presentation
                var itemsPickedFromThisPresentation = 0;
                var ordersToRemove = new List<OrderGroup>();

                foreach (var order in remainingOrders.ToList())
                {
                    var itemsFromThisRack = 0;
                    var allSkusFulfilled = true;

                    // Check each order line against this rack
                    foreach (var orderLine in order.OrderLines)
                    {
                        if (bestRackForRound.rack.Skus.Contains(orderLine.Sku))
                        {
                            itemsFromThisRack += orderLine.Quantity;
                            presentation.PickedItems.Add(new PickedItem
                            {
                                Sku = orderLine.Sku,
                                Quantity = orderLine.Quantity,
                                OrderId = order.OrderId
                            });
                        }
                        else
                        {
                            allSkusFulfilled = false;
                        }
                    }

                    itemsPickedFromThisPresentation += itemsFromThisRack;

                    // In Pick-to-Order, remove order only if ALL its SKUs can be fulfilled by this rack
                    if (allSkusFulfilled)
                    {
                        ordersToRemove.Add(order);
                    }
                }

                presentation.ItemsPickedFromThisRack = itemsPickedFromThisPresentation;
                
                // Only add presentation if items were actually picked
                if (itemsPickedFromThisPresentation > 0)
                {
                    rackPresentations.Add(presentation);
                }

                // Remove fully fulfilled orders
                foreach (var orderToRemove in ordersToRemove)
                {
                    remainingOrders.Remove(orderToRemove);
                }

                // Safety check to prevent infinite loops
                if (!ordersToRemove.Any() && remainingOrders.Any())
                {
                    _logger.LogWarning("Unable to fulfill remaining orders - some SKUs may not be available on any rack");
                    break;
                }
            }

            result.RackPresentations = rackPresentations;
            result.TotalRackPresentations = rackPresentations.Count; // R in formula
            result.SumOfItemsPickedFromAllRacks = rackPresentations.Sum(rp => rp.ItemsPickedFromThisRack); // Σ(I_r_i)
            
            // Apply the exact formula: HR = Σ(I_r_i) / T
            result.HitRate = result.TotalItems > 0 
                ? (double)result.SumOfItemsPickedFromAllRacks / result.TotalItems 
                : 0;

            _logger.LogInformation("Wave processed: {TotalPresentations} presentations, {ItemsPicked} items picked from racks, {TotalItems} total items, Hit Rate: {HitRate}", 
                result.TotalRackPresentations, result.SumOfItemsPickedFromAllRacks, result.TotalItems, result.HitRate);

            return result;
        }

        private (Rack? rack, int potentialItems) FindBestRackForCurrentOrders(List<Rack> racks, List<OrderGroup> remainingOrders)
        {
            var bestRack = racks
                .Select(rack => new
                {
                    Rack = rack,
                    PotentialItems = remainingOrders
                        .SelectMany(order => order.OrderLines)
                        .Where(line => rack.Skus.Contains(line.Sku))
                        .Sum(line => line.Quantity)
                })
                .OrderByDescending(x => x.PotentialItems)
                .FirstOrDefault();

            return bestRack != null ? (bestRack.Rack, bestRack.PotentialItems) : (null, 0);
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