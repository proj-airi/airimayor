using CS2MCP;
using Unity.Mathematics;
using Xunit;

public sealed class CityOsmSurfaceTests
{
    [Fact]
    public void Terrain_samples_requested_boundaries_and_traverses_x_before_z()
    {
        var bounds = new float4(-3f, 10f, 5f, 25f);
        OsmTerrainHeightfield terrain = CityOsmSurface.SampleTerrain(bounds, 5f, point => point.x + 100f * point.y);

        Assert.Equal((-3d, 10d, 5d, 25d), (terrain.XMin, terrain.ZMin, terrain.XMax, terrain.ZMax));
        Assert.Equal((3, 4), (terrain.Columns, terrain.Rows));
        Assert.Equal((4d, 5d), (terrain.XStep, terrain.ZStep));
        Assert.Equal("z-ascending-x-fastest", terrain.SampleOrder);
        Assert.Equal(new float[] { 997, 1001, 1005, 1497, 1501, 1505, 1997, 2001, 2005, 2497, 2501, 2505 }, terrain.Heights);
    }

    [Fact]
    public void Large_terrain_extents_keep_a_bounded_heightfield()
    {
        OsmTerrainHeightfield terrain = CityOsmSurface.SampleTerrain(new float4(-10000f, -20f, 10000f, 20f), 5f, _ => 20f);

        Assert.Equal((193, 9), (terrain.Columns, terrain.Rows));
        Assert.Equal(193 * 9, terrain.Heights.Length);
    }

    [Fact]
    public void A_waterbody_crossing_the_selection_closes_at_the_true_selection_edges()
    {
        var map = new OsmDocument();
        CityOsmSurface.AddWater(map, new float4(-3f, -9f, 15f, 9f), 4f, point => 0.05f + (point.x - 2f) * 0.01f);
        OsmDocument parsed = OsmDocument.Read(map.Write());

        OsmWay shoreline = Assert.Single(parsed.Ways.Values);
        Assert.Equal("water", shoreline.Tags["natural"]);
        Assert.Equal(shoreline.NodeIds[0], shoreline.NodeIds[^1]);
        var vertices = shoreline.NodeIds.Select(id => CityOsm.Position(parsed.Nodes[id]).xz).ToList();
        Assert.Equal(2f, vertices.Min(point => point.x), 4);
        Assert.Equal(15f, vertices.Max(point => point.x));
        Assert.Equal(-9f, vertices.Min(point => point.y));
        Assert.Equal(9f, vertices.Max(point => point.y));
        Assert.Equal(4, vertices.Distinct().Count());
    }

    [Fact]
    public void Islands_are_inner_members_without_consuming_a_pond_inside_the_island()
    {
        var map = new OsmDocument();
        float Depth(float2 point)
        {
            float radius = math.length(point);
            return radius < 4f || radius > 13f && radius < 26f ? 1f : 0f;
        }
        CityOsmSurface.AddWater(map, new float4(-30f, -30f, 30f, 30f), 2f, Depth);
        OsmDocument parsed = OsmDocument.Read(map.Write());

        OsmRelation water = Assert.Single(parsed.Relations.Values);
        Assert.Equal("multipolygon", water.Tags["type"]);
        Assert.Equal("water", water.Tags["natural"]);
        Assert.Equal(new[] { "outer", "inner" }, water.Members.Select(member => member.Role));
        Assert.All(water.Members, member =>
        {
            OsmWay way = parsed.Ways[member.Id];
            Assert.Empty(way.Tags);
            Assert.Equal(way.NodeIds[0], way.NodeIds[^1]);
        });
        Assert.Single(parsed.Ways.Values, way => way.Tags.TryGetValue("natural", out string tag) && tag == "water");
    }

    [Fact]
    public void A_threshold_equal_island_remains_inside_the_water_outline()
    {
        var map = new OsmDocument();
        CityOsmSurface.AddWater(map, new float4(0f, 0f, 4f, 4f), 1f, position =>
        {
            if (math.all(position == 2f)) return 0.05f;
            return math.all(position > 0f) && math.all(position < 4f) ? 1f : 0f;
        });
        var reopened = OsmDocument.Read(map.Write());

        OsmRelation water = Assert.Single(reopened.Relations.Values);
        OsmWay island = reopened.Ways[Assert.Single(water.Members, member => member.Role == "inner").Id];
        OsmWay outline = reopened.Ways[Assert.Single(water.Members, member => member.Role == "outer").Id];
        var islandPoints = island.NodeIds.Select(id => CityOsm.Position(reopened.Nodes[id]).xz).ToList();
        var outlinePoints = outline.NodeIds.Select(id => CityOsm.Position(reopened.Nodes[id]).xz).ToList();
        Assert.Equal(4, islandPoints.Distinct().Count());
        Assert.All(islandPoints, point =>
        {
            Assert.InRange(point.x, 1.999f, 2.001f);
            Assert.InRange(point.y, 1.999f, 2.001f);
            Assert.DoesNotContain(point, outlinePoints);
        });
    }

    [Theory]
    [InlineData(0.06f, 2)]
    [InlineData(1f, 1)]
    public void Bilinear_saddle_resolves_disconnected_banks_from_actual_depth(float wetDepth, int expectedPatches)
    {
        var map = new OsmDocument();
        CityOsmSurface.AddWater(map, new float4(0f, 0f, 10f, 10f), 10f, position => position.x == position.y ? wetDepth : 0f);

        Assert.Equal(expectedPatches, map.Ways.Count);
        Assert.Empty(map.Relations);
        Assert.All(map.Ways.Values, way => Assert.Equal(way.NodeIds[0], way.NodeIds[^1]));
    }

    [Fact]
    public void Long_shorelines_are_joined_by_shared_member_endpoints()
    {
        var map = new OsmDocument();
        float Depth(float2 point)
        {
            int x = (int)point.x, z = (int)point.y;
            bool wet = x > 0 && x < 508 && z > 0 && z < 42
                && (z is >= 20 and <= 22 || x % 2 == 0);
            return wet ? 1f : 0f;
        }
        CityOsmSurface.AddWater(map, new float4(0f, 0f, 509f, 43f), 1f, Depth);
        OsmDocument parsed = OsmDocument.Read(map.Write());

        OsmRelation water = Assert.Single(parsed.Relations.Values);
        OsmWay[] segments = water.Members.Where(member => member.Role == "outer").Select(member => parsed.Ways[member.Id]).ToArray();
        Assert.True(segments.Length > 1);
        Assert.All(segments, way => Assert.InRange(way.NodeIds.Count, 2, 2000));
        for (int i = 0; i < segments.Length; i++)
            Assert.Equal(segments[i].NodeIds[^1], segments[(i + 1) % segments.Length].NodeIds[0]);
    }
}
