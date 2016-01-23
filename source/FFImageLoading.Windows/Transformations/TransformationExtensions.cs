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
            // TODO
            return null;
        }

        public static int[] ToIntPixelArray(this byte[] pixelArray)
        {
            // TODO
            return null;
        }
    }
}
