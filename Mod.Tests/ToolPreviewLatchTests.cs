using System;
using Xunit;

namespace airimayor.Agent
{
    public sealed class ToolPreviewLatchTests
    {
        private static readonly byte[] Jpeg = { 1, 2, 3 };

        [Fact]
        public void Offer_then_claim_pairs_city_prefix()
        {
            var latch = new ToolPreviewLatch(null);
            latch.Offer("map_image", "{}", Jpeg);
            string uri = latch.Claim("call-1", "city_map_image", "{}");
            Assert.Equal(ToolPreview.DataUri(Jpeg), uri);
            Assert.Null(latch.Claim("call-2", "city_map_image", "{}"));
        }

        [Fact]
        public void Claim_then_offer_ignores_argument_key_order()
        {
            string seenId = null;
            string seenUri = null;
            var latch = new ToolPreviewLatch((id, uri) =>
            {
                seenId = id;
                seenUri = uri;
            });
            Assert.Null(latch.Claim("call-1", "map_image", "{\"y\":2,\"x\":1}"));
            latch.Offer("map_image", "{\"x\":1,\"y\":2}", Jpeg);
            Assert.Equal("call-1", seenId);
            Assert.Equal(ToolPreview.DataUri(Jpeg), seenUri);
        }

        [Fact]
        public void Two_calls_do_not_share_one_preview()
        {
            string seenId = null;
            var latch = new ToolPreviewLatch((id, uri) => seenId = id);
            Assert.Null(latch.Claim("a", "map_image", "{\"n\":1}"));
            Assert.Null(latch.Claim("b", "map_image", "{\"n\":2}"));
            latch.Offer("map_image", "{\"n\":2}", Jpeg);
            Assert.Equal("b", seenId);
            Assert.Null(latch.Claim("a", "map_image", "{\"n\":1}"));
        }

        [Fact]
        public void Oversized_jpeg_yields_no_image()
        {
            var jpeg = new byte[(256 * 1024) + 1];
            var latch = new ToolPreviewLatch((id, uri) => throw new InvalidOperationException("matched"));
            latch.Offer("map_image", "{}", jpeg);
            Assert.Null(latch.Claim("call-1", "map_image", "{}"));
            Assert.Null(ToolPreview.DataUri(jpeg));
            Assert.Null(ToolPreview.DataUri(null));
            Assert.StartsWith("data:image/jpeg;base64,", ToolPreview.DataUri(Jpeg));
        }
    }
}
