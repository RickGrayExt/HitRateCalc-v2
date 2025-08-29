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
            var startTime = DateTime.UtcNow;
            _logger.LogInformation("Starting hit rate calculation with {OrderCount} orders", orders.Count);

            // Group orders by customer and time to create order groups
            var orderGroups = CreateOrderGroups(orders);
            _logger.LogInformation("Created {GroupCount} order groups", orderGroups.Count);

            // Create waves (batches of orders to process together)
            var waves = CreateWaves(orderGroups);
            _logger.LogInformation("Created {WaveCount} waves", waves.Count);

            var totalUnits = 0;
            var totalRackPresentations = 0;
            var waveResults = new List<WaveResult>();

            for (int waveIndex = 0; waveIndex < waves.Count; waveIndex++)
            {
                var wave = waves[waveIndex];
                var waveResult = ProcessWave(wave, maxOrdersPerStation, numberOfStations, maxSkusPerRack, waveIndex + 1);
                totalUnits += waveResult.UnitsPickedInWave;
                totalRackPresentations += waveResult.RackPresentationsInWave;
                waveResults.Add(waveResult);
            }

            var hitRate = totalRackPresentations > 0 ? (double)totalUnits / totalRackPresentations : 0;
            var endTime = DateTime.UtcNow;
            
            _logger.LogInformation("Calculation complete. Hit rate: {HitRate}, Duration: {Duration}ms", 
                hitRate, (endTime - startTime).TotalMilliseconds);

            return hitRate;
        }

        public async Task<double> CalculatePickToOrderHitRateAsync(
            List<Order> orders, 
            int maxSkusPerRack)
        {
            var startTime = DateTime.UtcNow;
            _logger.LogInformation("Starting pick-to-order hit rate calculation with {OrderCount} orders", orders.Count);

            // Group orders by customer and time to create order groups
            var orderGroups = CreateOrderGroups(orders);
            _logger.LogInformation("Created {GroupCount} order groups", orderGroups.Count);

            // Create waves (batches of orders to process together)
            var waves = CreateWaves(orderGroups);
            _logger.LogInformation("Created {WaveCount} waves", waves.Count);

            var totalOrderLines = 0;
            var totalRackPresentations = 0;

            foreach (var wave in waves)
            {
                var racks = CreateRacks(wave, maxSkusPerRack);
                var waveOrderLines = 0;
                var waveRackPresentations = 0;

                // For each order in the wave, calculate how many rack presentations are needed
                foreach (var orderGroup in wave.OrderGroups)
                {
                    var orderLines = orderGroup.OrderLines.Count;
                    var requiredSkus = orderGroup.UniqueSkus;
                    
                    // Find how many racks are needed to fulfill this order
                    var racksNeeded = racks.Where(r => r.Skus.Any(sku => requiredSkus.Contains(sku))).Count();
                    
                    waveOrderLines += orderLines;
                    waveRackPresentations += racksNeeded;
                }

                totalOrderLines += waveOrderLines;
                totalRackPresentations += waveRackPresentations;
            }

            var hitRate = totalRackPresentations > 0 ? (double)totalOrderLines / totalRackPresentations : 0;
            var endTime = DateTime.UtcNow;
            
            _logger.LogInformation("Pick-to-order calculation complete. Hit rate: {HitRate}, Duration: {Duration}ms", 
                hitRate, (endTime - startTime).TotalMilliseconds);

            return hitRate;
        }

        private List<OrderGroup> CreateOrderGroups(List<Order> orders, int waveDurationHours)
        {
            return orders
                .GroupBy(o => new
                {
                    o.CustomerId,
                    WaveStart = o.OrderDateTime.AddHours(-(o.OrderDateTime.Hour % waveDurationHours))
                })
                .Select(g => new OrderGroup
                {
                    OrderId = $"{g.Key.CustomerId}_{g.Key.WaveStart:yyyyMMddHHmm}",
                    OrderDateTime = g.Min(o => o.OrderDateTime),
                    OrderLines = g.Select(o => new OrderLine
                    {
                        Sku = o.Product,
                        Quantity = o.Quantity
                    }).ToList(),
                    UniqueSkus = g.Select(o => o.Product).Distinct().ToList()
                })
                .ToList();
        }

        private List<Wave> CreateWaves(List<OrderGroup> orderGroups)
        {
            var waves = new List<Wave>();
            var currentWave = new Wave();
            
            // Simple wave creation - group orders by 2 hours
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
                        EndTime = orderHour.AddHours(2)
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

        private WaveResult ProcessWave(Wave wave, int maxOrdersPerStation, int numberOfStations, int maxSkusPerRack, int waveNumber)
        {
            var stations = CreateStations(numberOfStations, maxOrdersPerStation);
            var racks = CreateRacks(wave, maxSkusPerRack);

            // Assign orders to stations
            AssignOrdersToStations(wave.OrderGroups, stations);

            var totalUnits = 0;
            var totalRackPresentations = 0;
            var stationResults = new List<StationResult>();

            // Process each station
            foreach (var station in stations.Where(s => s.AssignedOrders.Any()))
            {
                var stationStartTime = DateTime.UtcNow;
                var rackPresentations = ProcessStation(station, racks);
                var stationEndTime = DateTime.UtcNow;
                
                var stationUnits = rackPresentations.Sum(rp => rp.UnitsPicked);
                var stationPresentations = rackPresentations.Count;
                
                totalUnits += stationUnits;
                totalRackPresentations += stationPresentations;
                
                // Create station result
                var stationResult = new StationResult
                {
                    StationId = station.StationId,
                    RackPresentations = rackPresentations,
                    TotalUnitsPicked = stationUnits,
                    TotalRackPresentations = stationPresentations,
                    StationHitRate = stationPresentations > 0 ? (double)stationUnits / stationPresentations : 0,
                    ProcessedOrders = station.AssignedOrders,
                    ProcessingTime = stationEndTime - stationStartTime
                };
                
                stationResults.Add(stationResult);
                
                // Also update the station's results
                station.StationResults = rackPresentations;
            }

            return new WaveResult
            {
                WaveNumber = waveNumber,
                StartTime = wave.StartTime,
                EndTime = wave.EndTime,
                UnitsPickedInWave = totalUnits,
                RackPresentationsInWave = totalRackPresentations,
                WaveHitRate = totalRackPresentations > 0 ? (double)totalUnits / totalRackPresentations : 0,
                StationResults = stationResults,
                TotalOrdersInWave = wave.OrderGroups.Count,
                UniqueSkusInWave = wave.OrderGroups.SelectMany(og => og.UniqueSkus).Distinct().ToList()
            };
        }

        private List<Station> CreateStations(int numberOfStations, int maxOrdersPerStation)
        {
            return Enumerable.Range(1, numberOfStations)
                .Select(i => new Station
                {
                    StationId = i,
                    MaxOrders = maxOrdersPerStation
                })
                .ToList();
        }

        private List<Rack> CreateRacks(Wave wave, int maxSkusPerRack)
        {
            var allSkus = wave.OrderGroups
                .SelectMany(og => og.UniqueSkus)
                .Distinct()
                .ToList();

            var racks = new List<Rack>();
            var rackId = 1;

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

        private void AssignOrdersToStations(List<OrderGroup> orderGroups, List<Station> stations)
        {
            foreach (var order in orderGroups)
            {
                // Find a station that already has overlapping SKUs
                var stationWithOverlap = stations
                    .Where(s => s.AssignedOrders.Any())
                    .OrderByDescending(s => s.AssignedOrders
                        .SelectMany(o => o.UniqueSkus)
                        .Intersect(order.UniqueSkus).Count())
                    .FirstOrDefault();

                Station targetStation = null;

                if (stationWithOverlap != null && stationWithOverlap.CanTakeOrder)
                {
                    targetStation = stationWithOverlap;
                }
                else
                {
                    // Otherwise, fall back to the first available station
                    targetStation = stations.FirstOrDefault(s => s.CanTakeOrder);
                }

                if (targetStation != null)
                {
                    targetStation.AssignedOrders.Add(order);
                }
                else
                {
                    // If all stations are full, assign to least busy
                    var leastBusyStation = stations.OrderBy(s => s.AssignedOrders.Count).First();
                    leastBusyStation.AssignedOrders.Add(order);
                    _logger.LogWarning("All stations at capacity, assigning order {OrderId} to station {StationId} with {OrderCount} orders",
                        order.OrderId, leastBusyStation.StationId, leastBusyStation.AssignedOrders.Count);
                }
            }
        }


        private List<RackPresentation> ProcessStation(Station station, List<Rack> racks)
        {
            var rackPresentations = new List<RackPresentation>();
            var remainingOrders = new List<OrderGroup>(station.AssignedOrders);

            while (remainingOrders.Any())
            {
                var requiredSkus = remainingOrders
                    .SelectMany(o => o.UniqueSkus)
                    .Distinct()
                    .ToList();

                // Find the best rack for current requirements
                var bestRack = racks
                    .Where(r => r.Skus.Any(sku => requiredSkus.Contains(sku)))
                    .OrderByDescending(r => r.Skus.Count(sku => requiredSkus.Contains(sku)))
                    .FirstOrDefault();

                if (bestRack == null) 
                {
                    _logger.LogWarning("No suitable rack found for remaining orders at station {StationId}", station.StationId);
                    break;
                }

                var presentation = new RackPresentation
                {
                    RackId = bestRack.RackId,
                    StationId = station.StationId,
                    AvailableSkus = bestRack.Skus,
                    PresentationTime = DateTime.UtcNow
                };

                // Calculate units picked and fulfilled orders
                var fulfilledOrders = new List<OrderGroup>();
                var unitsPicked = 0;

                foreach (var order in remainingOrders.ToList())
                {
                    var orderSkusOnRack = order.UniqueSkus
                        .Where(sku => bestRack.Skus.Contains(sku))
                        .ToList();

                    if (orderSkusOnRack.Any())
                    {
                        var unitsFromThisOrder = order.OrderLines
                            .Where(ol => orderSkusOnRack.Contains(ol.Sku))
                            .Sum(ol => ol.Quantity);

                        unitsPicked += unitsFromThisOrder;

                        // If all SKUs for this order are available on this rack, mark as fulfilled
                        if (order.UniqueSkus.All(sku => bestRack.Skus.Contains(sku)))
                        {
                            fulfilledOrders.Add(order);
                        }
                    }
                }

                presentation.UnitsPicked = unitsPicked;
                presentation.FulfilledOrders = fulfilledOrders;
                presentation.IsCompleted = fulfilledOrders.Any();
                rackPresentations.Add(presentation);

                // Remove fulfilled orders from remaining
                foreach (var fulfilledOrder in fulfilledOrders)
                {
                    remainingOrders.Remove(fulfilledOrder);
                }

                // If no progress was made, break to avoid infinite loop
                if (unitsPicked == 0 && !fulfilledOrders.Any())
                {
                    _logger.LogWarning("No progress made on remaining orders at station {StationId}, breaking loop", station.StationId);
                    break;
                }
            }

            return rackPresentations;
        }
    }
}