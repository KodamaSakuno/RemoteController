using System;
using System.Threading.Tasks;

namespace RemoteController.Client.Services;

/// <summary>Pixel conversion helpers for decoded frames (NV12 or BGRA) into BGRA display buffers.</summary>
public static class Nv12Converter
{
    /// <summary>
    /// Converts the top-left crop of an NV12 frame to BGRA (BT.601 studio swing).
    /// The NV12 planes are tightly packed at <paramref name="alignedWidth"/> stride.
    /// Rows are independent, so convert them in parallel — a scalar per-pixel loop is the
    /// client's throughput bottleneck at high resolutions otherwise.
    /// </summary>
    public static unsafe void ToBgra(
        byte* nv12, int alignedWidth, int alignedHeight,
        byte* bgra, int bgraStride, int cropWidth, int cropHeight)
    {
        var uvPlane = nv12 + alignedWidth * alignedHeight;
        Parallel.For(0, cropHeight, y =>
        {
            var yRow = nv12 + y * alignedWidth;
            var uvRow = uvPlane + y / 2 * alignedWidth;
            var dst = bgra + y * bgraStride;
            for (var x = 0; x < cropWidth; x++)
            {
                var c = yRow[x] - 16;
                var d = uvRow[x & ~1] - 128;
                var e = uvRow[(x & ~1) + 1] - 128;

                var r = (298 * c + 409 * e + 128) >> 8;
                var g = (298 * c - 100 * d - 208 * e + 128) >> 8;
                var b = (298 * c + 516 * d + 128) >> 8;

                dst[x * 4] = (byte)(b < 0 ? 0 : b > 255 ? 255 : b);
                dst[x * 4 + 1] = (byte)(g < 0 ? 0 : g > 255 ? 255 : g);
                dst[x * 4 + 2] = (byte)(r < 0 ? 0 : r > 255 ? 255 : r);
                dst[x * 4 + 3] = 255;
            }
        });
    }

    /// <summary>Copies the top-left crop of a tightly packed BGRA frame.</summary>
    public static unsafe void CopyBgra(
        byte* src, int srcStridePixels,
        byte* dst, int dstStride, int cropWidth, int cropHeight)
    {
        var rowBytes = cropWidth * 4;
        for (var y = 0; y < cropHeight; y++)
            Buffer.MemoryCopy(
                src + (long)y * srcStridePixels * 4,
                dst + (long)y * dstStride, rowBytes, rowBytes);
    }
}
