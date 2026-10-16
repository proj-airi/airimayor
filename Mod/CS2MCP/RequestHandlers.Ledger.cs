using Game.City;
using Game.Simulation;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        public const string LedgerRoute = "/record/ledger";

        /// <summary>
        /// City ledger the session record keeps before and after each write
        /// (ADR 0047). Not model-facing: no catalog row names this route.
        /// Counts are top-level, non-temporary objects.
        /// </summary>
        private BridgeResponse ReadLedger()
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }
            SimulationSystem simulation = World.GetOrCreateSystemManaged<SimulationSystem>();
            return BridgeResponse.Json(new
            {
                frame = simulation.frameIndex,
                paused = simulation.selectedSpeed <= 0f,
                money = World.GetOrCreateSystemManaged<CitySystem>().moneyAmount,
                developmentPoints = World.GetOrCreateSystemManaged<DevTreeSystem>().points,
                buildings = PlacedBuildingQuery.CalculateEntityCount(),
                networkEdges = PlacedRoadQuery.CalculateEntityCount(),
                transitLines = TransitLineQuery.CalculateEntityCount(),
            });
        }
    }
}
