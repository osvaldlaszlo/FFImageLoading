using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.UI;

namespace FFImageLoading.Transformations
{
    public static class TransformationExtensions
    {
        public static int ToInt(this Color color)
        {
            var col = 0;

            if (color.A != 0)
            {
                var a = color.A + 1;
                col = (color.A << 24)
                  | ((byte)((color.R * a) >> 8) << 16)
                  | ((byte)((color.G * a) >> 8) << 8)
                  | ((byte)((color.B * a) >> 8));
            }

            return col;
        }

        public static void SetPixel(this byte[] pixelData, int x, int y, int color)
        {
            //TODO
        }

        public static void SetPixel(this byte[] pixelData, int x, int y, Color color)
        {
            pixelData.SetPixel(x, y, color.ToInt());
        }

        public static byte[] ToBytePixelArray(this int[] pixelArray)
        {
            byte[] result = new byte[pixelArray.Length * sizeof(int)];
            Buffer.BlockCopy(pixelArray, 0, result, 0, result.Length);
            return result;
        }

        public unsafe static int[] ToIntPixelArray(this byte[] pixelArray)
        {
            int length = pixelArray.Length / 4;
            int[] pixels = new int[length];

            fixed (byte* srcPtr = pixelArray)
            {
                fixed (int* dstPtr = pixels)
                {
                    for (var i = 0; i < length; i++)
                    {
                        dstPtr[i] = (srcPtr[i * 4 + 3] << 24)
                                  | (srcPtr[i * 4 + 2] << 16)
                                  | (srcPtr[i * 4 + 1] << 8)
                                  | srcPtr[i * 4 + 0];
                    }
                }
            }

            return pixels;
        }
    }
}
