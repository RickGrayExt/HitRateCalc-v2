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
            _logger.LogInformation("Starting hit rate calculation with {OrderCount} orders", orders.Count);

            // Group orders by customer and time to create order groups
            var orderGroups = CreateOrderGroups(orders);
            _logger.LogInformation("Created {GroupCount} order groups", orderGroups.Count);

            // Create waves (batches of orders to process together)
            var waves = CreateWaves(orderGroups);
            _logger.LogInformation("Created {WaveCount} waves", waves.Count);

            var totalUnits = 0;
            var totalRackPresentations = 0;

            foreach (var wave in waves)
            {
                var waveResult = ProcessWave(wave, maxOrdersPerStation, numberOfStations, maxSkusPerRack);
                totalUnits += waveResult.UnitsPickedInWave;
                totalRackPresentations += waveResult.RackPresentationsInWave;
            }

            var hitRate = totalRackPresentations > 0 ? (double)totalUnits / totalRackPresentations : 0;
            _logger.LogInformation("Calculation complete. Hit rate: {HitRate}", hitRate);

            return hitRate;
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
            
            // Simple wave creation - group orders by hour
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

        private WaveResult ProcessWave(Wave wave, int maxOrdersPerStation, int numberOfStations, int maxSkusPerRack)
        {
            var stations = CreateStations(numberOfStations, maxOrdersPerStation);
            var racks = CreateRacks(wave, maxSkusPerRack);

            // Assign orders to stations
            AssignOrdersToStations(wave.OrderGroups, stations);

            var totalUnits = 0;
            var totalRackPresentations = 0;

            // Process each station
            foreach (var station in stations.Where(s => s.AssignedOrders.Any()))
            {
                var rackPresentations = ProcessStation(station, racks);
                totalUnits += rackPresentations.Sum(rp => rp.UnitsPicked);
                totalRackPresentations += rackPresentations.Count;
            }

            return new WaveResult
            {
                UnitsPickedInWave = totalUnits,
                RackPresentationsInWave = totalRackPresentations,
                WaveHitRate = totalRackPresentations > 0 ? (double)totalUnits / totalRackPresentations : 0
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
                var station = stations.FirstOrDefault(s => s.CanTakeOrder);
                if (station != null)
                {
                    station.AssignedOrders.Add(order);
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

                if (bestRack == null) break;

                var presentation = new RackPresentation
                {
                    RackId = bestRack.RackId,
                    StationId = station.StationId,
                    AvailableSkus = bestRack.Skus
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
                            remainingOrders.Remove(order);
                        }
                    }
                }

                presentation.UnitsPicked = unitsPicked;
                presentation.FulfilledOrders = fulfilledOrders;
                rackPresentations.Add(presentation);

                // Remove fulfilled orders from remaining
                foreach (var fulfilledOrder in fulfilledOrders)
                {
                    remainingOrders.Remove(fulfilledOrder);
                }

                // If no progress was made, break to avoid infinite loop
                if (unitsPicked == 0 && !fulfilledOrders.Any())
                {
                    break;
                }
            }

            return rackPresentations;
        }
    }
}