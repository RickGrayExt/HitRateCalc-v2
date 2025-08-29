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
            return await CalculatePickToOrderHitRateWithStationsAsync(orders, maxSkusPerRack, maxOrdersPerStation, numberOfStations);
        }

        public async Task<double> CalculatePickToOrderHitRateAsync(
            List<Order> orders, 
            int maxSkusPerRack)
        {
            return await CalculatePickToOrderHitRateWithStationsAsync(orders, maxSkusPerRack, int.MaxValue, 1);
        }

        public async Task<double> CalculatePickToOrderHitRateWithStationsAsync(
            List<Order> orders, 
            int maxSkusPerRack,
            int maxOrdersPerStation = int.MaxValue,
            int numberOfStations = 1)
        {
            _logger.LogInformation("Starting Pick-to-Order hit rate calculation with {OrderCount} orders, {MaxOrdersPerStation} max orders per station, {NumberOfStations} stations", 
                orders.Count, maxOrdersPerStation, numberOfStations);

            // Create order groups
            var orderGroups = CreateOrderGroups(orders);
            _logger.LogInformation("Created {GroupCount} order groups", orderGroups.Count);

            // Create waves
            var waves = CreateWaves(orderGroups);
            _logger.LogInformation("Created {WaveCount} waves", waves.Count);

            var allResults = new List<PickToOrderResult>();

            foreach (var wave in waves)
            {
                var waveResult = CalculatePickToOrderHitRateForWave(wave, maxSkusPerRack, maxOrdersPerStation, numberOfStations);
                allResults.Add(waveResult);
            }

            // Calculate overall hit rate as the average of all wave hit rates
            // Each wave's hit rate is already the average units picked per rack presentation
            var overallHitRate = allResults.Count > 0 ? allResults.Average(r => r.HitRate) : 0;
            
            _logger.LogInformation("Pick-to-Order calculation complete. Overall average hit rate: {HitRate}", overallHitRate);
            return overallHitRate;
        }

        private PickToOrderResult CalculatePickToOrderHitRateForWave(
            Wave wave, 
            int maxSkusPerRack, 
            int maxOrdersPerStation, 
            int numberOfStations)
        {
            var result = new PickToOrderResult();
            
            // T = Total items in the wave
            result.TotalItems = wave.TotalUnits;
            
            // Create racks based on unique SKUs in the wave
            var racks = CreateRacksForWave(wave, maxSkusPerRack);
            _logger.LogInformation("Created {RackCount} racks for wave", racks.Count);

            // Allocate orders to stations
            var stationAllocations = AllocateOrdersToStations(wave.OrderGroups, maxOrdersPerStation, numberOfStations);
            _logger.LogInformation("Allocated orders to {StationCount} stations", stationAllocations.Count);

            var allStationResults = new List<StationResult>();

            // Process each station separately
            foreach (var station in stationAllocations)
            {
                var stationResult = ProcessStation(station, racks);
                allStationResults.Add(stationResult);
                
                _logger.LogInformation("Station {StationId}: {OrderCount} orders, {PresentationCount} presentations, {ItemsPicked} items picked", 
                    station.StationId, station.Orders.Count, stationResult.RackPresentations.Count, stationResult.TotalItemsPicked);
            }

            // Aggregate results across all stations
            result.RackPresentations = allStationResults.SelectMany(sr => sr.RackPresentations).ToList();
            result.TotalRackPresentations = result.RackPresentations.Count;
            result.SumOfItemsPickedFromAllRacks = allStationResults.Sum(sr => sr.TotalItemsPicked);
            result.StationResults = allStationResults;

            // Calculate hit rate as average units picked per rack presentation across all stations
            result.HitRate = result.TotalRackPresentations > 0 
                ? (double)result.SumOfItemsPickedFromAllRacks / result.TotalRackPresentations 
                : 0;

            _logger.LogInformation("Wave processed: {StationCount} stations, {TotalPresentations} total presentations, {ItemsPicked} total items picked, Average per presentation: {HitRate}", 
                allStationResults.Count, result.TotalRackPresentations, result.SumOfItemsPickedFromAllRacks, result.HitRate);

            return result;
        }

        private List<Station> AllocateOrdersToStations(List<OrderGroup> orderGroups, int maxOrdersPerStation, int numberOfStations)
        {
            var stations = new List<Station>();
            
            // Initialize stations
            for (int i = 1; i <= numberOfStations; i++)
            {
                stations.Add(new Station 
                { 
                    StationId = i, 
                    Orders = new List<OrderGroup>(),
                    MaxOrders = maxOrdersPerStation
                });
            }

            // Strategy: Try to group orders with similar SKUs on the same station for better efficiency
            var remainingOrders = orderGroups.ToList();
            int currentStationIndex = 0;

            while (remainingOrders.Any())
            {
                var currentStation = stations[currentStationIndex];
                
                // If station is at capacity, move to next station
                if (currentStation.Orders.Count >= maxOrdersPerStation)
                {
                    currentStationIndex = (currentStationIndex + 1) % numberOfStations;
                    
                    // If all stations are full, we need to process in batches
                    if (stations.All(s => s.Orders.Count >= maxOrdersPerStation))
                    {
                        _logger.LogWarning("All stations are at capacity. Processing current batch first.");
                        break;
                    }
                    continue;
                }

                // Find the best order to add to current station (one with most SKU overlap)
                var bestOrder = FindBestOrderForStation(currentStation, remainingOrders);
                
                currentStation.Orders.Add(bestOrder);
                remainingOrders.Remove(bestOrder);

                // Move to next station for better distribution
                currentStationIndex = (currentStationIndex + 1) % numberOfStations;
            }

            // Remove empty stations
            return stations.Where(s => s.Orders.Any()).ToList();
        }

        private OrderGroup FindBestOrderForStation(Station station, List<OrderGroup> availableOrders)
        {
            if (!station.Orders.Any())
            {
                // If station is empty, just take the first available order
                return availableOrders.First();
            }

            // Find order with most SKU overlap with existing orders in the station
            var stationSkus = station.Orders.SelectMany(o => o.UniqueSkus).Distinct().ToHashSet();
            
            var bestOrder = availableOrders
                .Select(order => new
                {
                    Order = order,
                    OverlapCount = order.UniqueSkus.Count(sku => stationSkus.Contains(sku))
                })
                .OrderByDescending(x => x.OverlapCount)
                .ThenBy(x => x.Order.OrderDateTime) // Tie-breaker: earlier orders first
                .First()
                .Order;

            return bestOrder;
        }

        private StationResult ProcessStation(Station station, List<Rack> racks)
        {
            var stationResult = new StationResult
            {
                StationId = station.StationId,
                RackPresentations = new List<RackPresentation>()
            };

            // Track which orders have been fully processed at this station
            var remainingOrders = station.Orders.ToList();
            int presentationId = 1;

            // Continue until all orders at this station are fulfilled
            while (remainingOrders.Any())
            {
                // Find the rack that can fulfill the most items from remaining orders
                var bestRackForRound = FindBestRackForCurrentOrders(racks, remainingOrders);
                
                if (bestRackForRound.rack == null || bestRackForRound.potentialItems == 0)
                {
                    _logger.LogWarning("No rack found to fulfill remaining orders at station {StationId}", station.StationId);
                    break;
                }

                var presentation = new RackPresentation
                {
                    RackId = bestRackForRound.rack.RackId,
                    PresentationId = presentationId++,
                    StationId = station.StationId,
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
                    stationResult.RackPresentations.Add(presentation);
                }

                // Remove fully fulfilled orders
                foreach (var orderToRemove in ordersToRemove)
                {
                    remainingOrders.Remove(orderToRemove);
                }

                // Safety check to prevent infinite loops
                if (!ordersToRemove.Any() && remainingOrders.Any())
                {
                    _logger.LogWarning("Unable to fulfill remaining orders at station {StationId} - some SKUs may not be available on any rack", station.StationId);
                    break;
                }
            }

            stationResult.TotalItemsPicked = stationResult.RackPresentations.Sum(rp => rp.ItemsPickedFromThisRack);
            
            return stationResult;
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

            _logger.LogInformation("Created {RackCount} racks with max {MaxSkus} SKUs per rack", racks.Count, maxSkusPerRack);
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

    // Additional model classes you'll need to add to your Models namespace
    public class Station
    {
        public int StationId { get; set; }
        public List<OrderGroup> Orders { get; set; } = new List<OrderGroup>();
        public int MaxOrders { get; set; }
    }

    public class StationResult
    {
        public int StationId { get; set; }
        public List<RackPresentation> RackPresentations { get; set; } = new List<RackPresentation>();
        public int TotalItemsPicked { get; set; }
    }
}