using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace CS2MCP
{
    /// <summary>
    /// MonoBehaviour helper that captures the screen at WaitForEndOfFrame —
    /// the only point in the frame where Unity reliably allows reading the
    /// back buffer (calling ScreenCapture from a system update returns null).
    /// Completes the BridgeRequest itself, so the handler returns no response.
    /// </summary>
    public sealed class ScreenshotRunner : MonoBehaviour
    {
        private static ScreenshotRunner s_Instance;

        public static ScreenshotRunner Ensure()
        {
            if (s_Instance == null)
            {
                var host = new GameObject("CS2MCP.ScreenshotRunner")
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };
                DontDestroyOnLoad(host);
                s_Instance = host.AddComponent<ScreenshotRunner>();
            }
            return s_Instance;
        }

        public void Capture(BridgeRequest request)
        {
            StartCoroutine(CaptureRoutine(request));
        }

        private IEnumerator CaptureRoutine(BridgeRequest request)
        {
            yield return new WaitForEndOfFrame();

            Texture2D captured = null;
            Texture2D output = null;
            try
            {
                captured = ScreenCapture.CaptureScreenshotAsTexture();
                if (captured == null)
                {
                    // Fallback: read the back buffer directly.
                    captured = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
                    captured.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0);
                    captured.Apply();
                }

                output = captured;
                bool skipEncode = false;
                if (request.TryGetInt("width", out int width))
                {
                    if (width < 320 || width > 1920)
                    {
                        request.Complete(BridgeResponse.Error(
                            BridgeErrorKind.InvalidArguments,
                            "width must be between 320 and 1920"));
                        skipEncode = true;
                    }
                    else if (width < captured.width)
                    {
                        output = Downscale(captured, width);
                    }
                }

                if (!skipEncode)
                {
                    // CaptureScreenshotAsTexture is one gamma too bright in a
                    // linear project. Encode those bytes directly and the file
                    // stays washed; undo that curve and write the array as-is.
                    byte[] display = ToDisplayBytes(output.GetPixels32());
                    byte[] png = ImageConversion.EncodeArrayToPNG(
                        display,
                        GraphicsFormat.R8G8B8A8_UNorm,
                        (uint)output.width,
                        (uint)output.height,
                        0u);
                    if (png == null || png.Length == 0)
                    {
                        request.Complete(BridgeResponse.Error(BridgeErrorKind.Internal, "PNG encode failed"));
                    }
                    else
                    {
                        request.Complete(BridgeResponse.Png(png, ThumbnailJpeg(display, output.width, output.height)));
                    }
                }
            }
            catch (Exception e)
            {
                request.Complete(BridgeResponse.Error(BridgeErrorKind.Internal, $"screenshot failed: {e.GetType().Name}: {e.Message}"));
            }
            finally
            {
                if (output != null && !ReferenceEquals(output, captured))
                {
                    Destroy(output);
                }
                if (captured != null)
                {
                    Destroy(captured);
                }
            }
        }

        private static byte[] ToDisplayBytes(Color32[] pixels)
        {
            var bytes = new byte[pixels.Length * 4];
            for (int i = 0; i < pixels.Length; i++)
            {
                Color32 pixel = pixels[i];
                int offset = i * 4;
                bytes[offset] = UndoGamma(pixel.r);
                bytes[offset + 1] = UndoGamma(pixel.g);
                bytes[offset + 2] = UndoGamma(pixel.b);
                bytes[offset + 3] = 255;
            }
            return bytes;
        }

        private static byte UndoGamma(byte encoded)
        {
            float value = encoded / 255f;
            float linear = value <= 0.04045f
                ? value / 12.92f
                : Mathf.Pow((value + 0.055f) / 1.055f, 2.4f);
            return (byte)Mathf.Clamp(Mathf.RoundToInt(linear * 255f), 0, 255);
        }

        private static byte[] ThumbnailJpeg(byte[] display, int width, int height)
        {
            const int maxWidth = 480;
            byte[] pixels = display;
            int targetWidth = width;
            int targetHeight = height;
            if (width > maxWidth)
            {
                targetWidth = maxWidth;
                targetHeight = Math.Max(1, (int)Math.Round(height * (double)maxWidth / width));
                pixels = BoxDownscale(display, width, height, targetWidth, targetHeight);
            }
            return ImageConversion.EncodeArrayToJPG(
                pixels,
                GraphicsFormat.R8G8B8A8_UNorm,
                (uint)targetWidth,
                (uint)targetHeight,
                0u,
                60);
        }

        private static byte[] BoxDownscale(byte[] source, int width, int height, int targetWidth, int targetHeight)
        {
            var destination = new byte[targetWidth * targetHeight * 4];
            for (int y = 0; y < targetHeight; y++)
            {
                int y0 = y * height / targetHeight;
                int y1 = Math.Max(y0 + 1, (y + 1) * height / targetHeight);
                for (int x = 0; x < targetWidth; x++)
                {
                    int x0 = x * width / targetWidth;
                    int x1 = Math.Max(x0 + 1, (x + 1) * width / targetWidth);
                    int red = 0;
                    int green = 0;
                    int blue = 0;
                    int count = 0;
                    for (int yy = y0; yy < y1; yy++)
                    {
                        int row = yy * width;
                        for (int xx = x0; xx < x1; xx++)
                        {
                            int index = (row + xx) * 4;
                            red += source[index];
                            green += source[index + 1];
                            blue += source[index + 2];
                            count++;
                        }
                    }
                    int offset = (y * targetWidth + x) * 4;
                    destination[offset] = (byte)(red / count);
                    destination[offset + 1] = (byte)(green / count);
                    destination[offset + 2] = (byte)(blue / count);
                    destination[offset + 3] = 255;
                }
            }
            return destination;
        }

        private static Texture2D Downscale(Texture2D source, int targetWidth)
        {
            int targetHeight = Mathf.Max(1, Mathf.RoundToInt((float)source.height * targetWidth / source.width));
            RenderTexture rt = RenderTexture.GetTemporary(targetWidth, targetHeight, 0);
            RenderTexture previous = RenderTexture.active;
            try
            {
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;
                var result = new Texture2D(targetWidth, targetHeight, TextureFormat.RGB24, false);
                result.ReadPixels(new Rect(0, 0, targetWidth, targetHeight), 0, 0);
                result.Apply();
                return result;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
            }
        }
    }
}
