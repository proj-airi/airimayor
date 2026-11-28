using System;
using System.Linq;
using Unity.Mathematics;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        private BridgeResponse ApplyOsmTerrainEdit(BridgeRequest request, float4 bounds, OsmDocument before,
            OsmDocument after, OsmTerrainLevel level, bool preview)
        {
            if (!airimayor.Setting.StaticAllowConstruction)
                return BridgeResponse.Error(BridgeErrorKind.Conflict, "City construction is disabled.");

            if (!preview && !request.Query.ContainsKey("terrainRevision"))
                return BridgeResponse.Error(BridgeErrorKind.InvalidArguments, "Preview this terrain edit and submit its terrain revision.");

            var tool = World.GetOrCreateSystemManaged<BridgeToolSystem>();
            string command = FormattableString.Invariant($"{level.WayId}/{level.TargetHeight:R}") + "\n" +
                string.Join("\n", level.Ring.Select(point => FormattableString.Invariant($"{point.x:R},{point.y:R}")));
            string Revision()
            {
                return CityOsmFile.Revision(m_System.CityLoadId, bounds, command + "\n" + tool.CaptureOsmTerrainStamp(level.Bounds));
            }

            string terrainRevision = Revision();
            if (!preview && request.Query["terrainRevision"] != terrainRevision)
                return BridgeResponse.Error(BridgeErrorKind.Conflict, "The ground or grading proposal changed. Preview it again.");

            BridgeResponse CheckCurrent()
            {
                if (!airimayor.Setting.StaticAllowConstruction)
                    return BridgeResponse.Error(BridgeErrorKind.Conflict, "City construction is disabled.");
                BridgeResponse district = CheckOsmRevision(request, bounds);
                if (district != null) return district;
                return Revision() == terrainRevision ? null
                    : BridgeResponse.Error(BridgeErrorKind.Conflict, "The ground changed. Preview the grading again.");
            }

            BridgeResponse Complete(OsmTerrainResult result)
            {
                var brush = result.BrushBounds;
                var terrain = new
                {
                    way = level.WayId,
                    targetHeight = level.TargetHeight,
                    transitionWidth = result.TransitionWidth,
                    brushBounds = new { xMin = brush.x, zMin = brush.y, xMax = brush.z, zMax = brush.w },
                    minimumHeight = result.MinimumHeight,
                    maximumHeight = result.MaximumHeight,
                    maximumError = result.MaximumError,
                    tolerance = result.Tolerance,
                    sampleCount = result.SampleCount,
                    appliedStrokes = result.AppliedStrokes,
                    reachedTarget = preview ? (bool?)null : result.ReachedTarget,
                };
                if (preview)
                {
                    return BridgeResponse.Json(new
                    {
                        preview = true, revision = request.Query["revision"], terrainRevision,
                        proposedChanges = OsmChange.Between(before, after).Write(), terrain,
                    });
                }
                string error = result.ReachedTarget ? null
                    : "The ground did not reach the target height. Check the measured elevations before retrying.";
                return OsmFileResponse(bounds, ReadOsmDocument(bounds), editBase: before, terrain: terrain, error: error);
            }

            return tool.TryQueueOsmTerrain(request, level, CheckCurrent, Complete, preview)
                ? null : BridgeResponse.Error(BridgeErrorKind.Conflict, "Another city operation is in progress.");
        }
    }
}
