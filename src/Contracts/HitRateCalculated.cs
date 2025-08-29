using System;

namespace Contracts
{
    public class HitRateCalculated
    {
        public Guid RunId { get; set; }
        public double HitRate { get; set; }
    }
}