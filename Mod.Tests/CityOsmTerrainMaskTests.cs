using System;
using CS2MCP;
using Unity.Mathematics;
using Xunit;

namespace Airimayor.Tests
{
    public sealed class CityOsmTerrainMaskTests
    {
        [Fact]
        public void Terrain_mask_preserves_concavity_and_tapers_inside_the_ring()
        {
            var level = new OsmTerrainLevel(10, new[]
            {
                new float2(0, 0), new float2(120, 0), new float2(120, 50),
                new float2(55, 50), new float2(55, 120), new float2(0, 120),
            }, new float4(0, 0, 120, 120), 30);

            CityOsmTerrainMask mask = CityOsmTerrainMask.Create(level, 3.5f, 1000);

            Assert.True(mask.IsLevelCore(new float2(90, 25)));
            Assert.True(mask.IsLevelCore(new float2(25, 90)));
            Assert.False(mask.IsLevelCore(new float2(90, 90)));
            Assert.False(mask.IsLevelCore(new float2(1, 25)));
            Assert.InRange(Opacity(mask, new float2(7, 25)), 1, 254);
            Assert.Equal(0, Opacity(mask, new float2(55, 75)));
            Assert.Equal(0, Opacity(mask, new float2(56, 75)));
        }

        [Fact]
        public void A_cut_narrower_than_one_texel_stays_clear()
        {
            var level = new OsmTerrainLevel(10, new[]
            {
                new float2(0, 0), new float2(120, 0), new float2(120, 120), new float2(61.05f, 120),
                new float2(61.05f, 35), new float2(60.95f, 35), new float2(60.95f, 120), new float2(0, 120),
            }, new float4(0, 0, 120, 120), 30);
            CityOsmTerrainMask mask = CityOsmTerrainMask.Create(level, 3.5f, 1000);

            Assert.Equal(0, Opacity(mask, new float2(61, 90)));
            Assert.True(mask.IsLevelCore(new float2(40, 90)));
            Assert.True(mask.IsLevelCore(new float2(90, 90)));
        }

        [Fact]
        public void Native_validated_circle_encloses_every_nonzero_mask_pixel()
        {
            var level = new OsmTerrainLevel(10, new[]
            {
                new float2(-150, -25), new float2(150, -25), new float2(150, 25), new float2(-150, 25),
            }, new float4(-150, -25, 150, 25), 30);
            CityOsmTerrainMask mask = CityOsmTerrainMask.Create(level, 3.5f, 1000);
            float size = mask.Size / mask.Resolution;
            float radius = mask.BrushBounds.z - mask.Center.x;

            for (int index = 0; index < mask.Pixels.Length; index++)
            {
                if (mask.Pixels[index] == 0) continue;
                float2 sample = (new float2(index % mask.Resolution, index / mask.Resolution) + 0.5f) * size - mask.Size * 0.5f;
                Assert.True(math.length(sample) + math.sqrt(2f) * size <= radius);
            }
        }

        [Fact]
        public void Oversize_terrain_requires_a_smaller_native_brush_scope()
        {
            var level = new OsmTerrainLevel(10, new[]
            {
                new float2(0, 0), new float2(1000, 0), new float2(1000, 1000), new float2(0, 1000),
            }, new float4(0, 0, 1000, 1000), 30);

            Assert.Throws<FormatException>(() => CityOsmTerrainMask.Create(level, 3.5f, 1000));
        }

        private static byte Opacity(CityOsmTerrainMask mask, float2 point)
        {
            int2 pixel = (int2)math.floor((point - mask.Center) * (mask.Resolution / mask.Size) + mask.Resolution * 0.5f);
            return mask.Pixels[pixel.y * mask.Resolution + pixel.x];
        }
    }
}
