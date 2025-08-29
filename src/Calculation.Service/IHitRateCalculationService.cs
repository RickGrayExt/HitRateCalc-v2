using Calculation.Service.Models;

namespace Calculation.Service.Services
{
    public interface IHitRateCalculationService
    {
        Task<double> CalculateHitRateAsync(
            List<Order> orders, 
            int maxOrdersPerStation, 
            int numberOfStations, 
            int maxSkusPerRack);
    }
}