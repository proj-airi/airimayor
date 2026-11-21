using Unity.Mathematics;

namespace CS2MCP
{
    internal readonly struct RoadPath
    {
        public RoadPath(float3 a, float3 b, float3 c, float3 d)
        {
            A = a;
            B = b;
            C = c;
            D = d;
        }

        public float3 A { get; }
        public float3 B { get; }
        public float3 C { get; }
        public float3 D { get; }

        public static RoadPath Straight(float3 start, float3 end)
        {
            float3 delta = end - start;
            return new RoadPath(start, start + delta / 3f, start + delta * (2f / 3f), end);
        }
    }

    internal static class NetworkCourseMath
    {
        public static float Length(RoadPath path)
        {
            const int steps = 16;
            float length = 0f;
            float3 previous = path.A;
            for (int i = 1; i <= steps; i++)
            {
                float3 point = Point(path, i / (float)steps);
                length += math.distance(previous, point);
                previous = point;
            }
            return length;
        }

        public static float3 Point(RoadPath path, float t)
        {
            float inverse = 1f - t;
            float inverseSquared = inverse * inverse;
            float tSquared = t * t;
            return path.A * (inverseSquared * inverse)
                + path.B * (3f * inverseSquared * t)
                + path.C * (3f * inverse * tSquared)
                + path.D * (tSquared * t);
        }
    }
}
