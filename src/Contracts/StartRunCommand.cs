using System;

namespace Contracts
{
    public record StartRunCommand
    {
        public string DatasetPath { get; init; } = string.Empty;
        public Guid RunId { get; init; }           // ← This is line 8
        public int MaxOrdersPerStation { get; init; } = 4;
        public int NumberOfStations { get; init; } = 4;
        public int MaxSkusPerRack { get; init; } = 50;
    }
}