using Colossal.Mathematics;
using Unity.Mathematics;

namespace CS2MCP
{
    internal static class CityOsmSpatial
    {
        public static float LotOverhang(float2 halfLot, Bounds3 geometry)
        {
            float3 half = new float3(halfLot.x, 0f, halfLot.y);
            return math.length(math.max(0f, math.max(geometry.min + half, half - geometry.max)));
        }

        public static Bounds2 NetworkReadBounds(Bezier4x2 curve, float2 start, float2 end)
        {
            Bounds2 bounds = MathUtils.Bounds(curve);
            return new Bounds2(math.min(bounds.min, math.min(start, end)) - 0.5f, math.max(bounds.max, math.max(start, end)) + 0.5f);
        }

        public static bool Intersects(Bounds2 bounds, Bezier4x2 curve)
        {
            if (MathUtils.Intersect(bounds, curve.a) || MathUtils.Intersect(bounds, curve.d)) return true;
            if (!MathUtils.Intersect(bounds, MathUtils.Bounds(curve))) return false;
            float2 lowerRight = new float2(bounds.max.x, bounds.min.y);
            float2 upperLeft = new float2(bounds.min.x, bounds.max.y);
            return MathUtils.Intersect(curve, new Line2.Segment(bounds.min, lowerRight), out _, 8)
                || MathUtils.Intersect(curve, new Line2.Segment(lowerRight, bounds.max), out _, 8)
                || MathUtils.Intersect(curve, new Line2.Segment(bounds.max, upperLeft), out _, 8)
                || MathUtils.Intersect(curve, new Line2.Segment(upperLeft, bounds.min), out _, 8);
        }
    }
}
