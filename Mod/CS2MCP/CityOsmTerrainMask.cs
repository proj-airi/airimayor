using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace CS2MCP
{
    internal readonly struct CityOsmTerrainMask
    {
        private const float ValidatedRadiusRatio = 0.4f;

        private CityOsmTerrainMask(float2 center, float size, int resolution, float transition, byte[] pixels)
        {
            Center = center;
            Size = size;
            Resolution = resolution;
            Transition = transition;
            Pixels = pixels;
        }

        public float2 Center { get; }
        public float Size { get; }
        public int Resolution { get; }
        public float Transition { get; }
        public byte[] Pixels { get; }

        public float4 BrushBounds => new float4(Center - Size * ValidatedRadiusRatio, Center + Size * ValidatedRadiusRatio);

        public static float BrushSize(float4 bounds, float terrainSpacing, float maximumBrushSize)
        {
            float size = (math.length((bounds.zw - bounds.xy) * 0.5f) + terrainSpacing) / ValidatedRadiusRatio;
            if (size > maximumBrushSize)
                throw new FormatException("That terrain area exceeds one native brush. Draw a smaller area.");
            return size;
        }

        public static CityOsmTerrainMask Create(OsmTerrainLevel level, float terrainSpacing, float maximumBrushSize)
        {
            float2 center = (level.Bounds.xy + level.Bounds.zw) * 0.5f;
            float size = BrushSize(level.Bounds, terrainSpacing, maximumBrushSize);
            int resolution = math.clamp((int)math.ceil(size / (terrainSpacing * 0.5f)), 32, 1024);
            float texel = size / resolution;
            float2 corner = center - size * 0.5f;
            int[] distance = new int[resolution * resolution];
            var crossing = new List<float>(level.Ring.Length);
            for (int row = 0; row < resolution; row++)
            {
                float z = corner.y + (row + 0.5f) * texel;
                crossing.Clear();
                for (int i = 0, previous = level.Ring.Length - 1; i < level.Ring.Length; previous = i++)
                {
                    float2 a = level.Ring[previous], b = level.Ring[i];
                    if ((a.y > z) != (b.y > z))
                        crossing.Add(a.x + (b.x - a.x) * ((z - a.y) / (b.y - a.y)));
                }
                crossing.Sort();
                for (int i = 0; i + 1 < crossing.Count; i += 2)
                {
                    int first = math.max(0, (int)math.ceil((crossing[i] - corner.x) / texel - 0.5f));
                    int pastLast = math.min(resolution, (int)math.ceil((crossing[i + 1] - corner.x) / texel - 0.5f));
                    for (int column = first; column < pastLast; column++)
                        distance[row * resolution + column] = resolution;
                }
            }

            MarkBoundary(level.Ring, corner, texel, distance, resolution);
            MeasureDistance(distance, resolution, 1);
            MeasureDistance(distance, resolution, -1);
            int taper = (int)math.ceil(terrainSpacing * 2f / texel);
            var pixels = new byte[distance.Length];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = (byte)(math.clamp(distance[i] - 2, 0, taper) * 255 / taper);
            return new CityOsmTerrainMask(center, size, resolution, (taper + 2) * texel, pixels);
        }

        public bool IsLevelCore(float2 point)
        {
            float2 coordinates = (point - Center) * (Resolution / Size) + (Resolution * 0.5f - 0.5f);
            int2 pixel = (int2)math.floor(coordinates);
            if (math.any(pixel < 0) || math.any(pixel >= Resolution - 1)) return false;
            int index = pixel.y * Resolution + pixel.x;
            return Pixels[index] == 255 && Pixels[index + 1] == 255
                && Pixels[index + Resolution] == 255 && Pixels[index + Resolution + 1] == 255;
        }

        private static void MarkBoundary(float2[] ring, float2 corner, float texel, int[] mask, int resolution)
        {
            for (int i = 0, previous = ring.Length - 1; i < ring.Length; previous = i++)
            {
                float2 a = ring[previous], b = ring[i];
                int steps = math.max(1, (int)math.ceil(2f * math.cmax(math.abs(b - a)) / texel));
                for (int step = 0; step <= steps; step++)
                {
                    float2 position = math.lerp(a, b, step / (float)steps);
                    int2 pixel = (int2)math.floor((position - corner) / texel);
                    mask[pixel.y * resolution + pixel.x] = 0;
                }
            }
        }

        private static void MeasureDistance(int[] distance, int size, int direction)
        {
            int first = direction > 0 ? 1 : size - 2;
            int last = direction > 0 ? size - 1 : 0;
            for (int row = first; row != last; row += direction)
            {
                for (int column = first; column != last; column += direction)
                {
                    int index = row * size + column;
                    if (distance[index] == 0) continue;
                    int previousRow = index - direction * size;
                    int nearest = math.min(distance[index - direction], distance[previousRow]);
                    nearest = math.min(nearest, distance[previousRow - 1]);
                    nearest = math.min(nearest, distance[previousRow + 1]);
                    distance[index] = math.min(distance[index], nearest + 1);
                }
            }
        }
    }
}
