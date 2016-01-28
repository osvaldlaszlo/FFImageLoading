using FFImageLoading.Transformations;
using System;
using Windows.UI;

namespace FFImageLoading.Work
{
    public class BitmapHolder : IBitmap
    {
        bool _optimizedBitmapHolder = false;

        [Obsolete("Use new optimized constructor")]
        public BitmapHolder(int[] pixels, int width, int height)
        {
            _pixels = pixels;

            Width = width;
            Height = height;
        }

        public BitmapHolder(byte[] pixels, int width, int height)
        {
            _optimizedBitmapHolder = true;
            _pixelData = pixels;

            Width = width;
            Height = height;
        }

        public int Height
        {
            get; private set; 
        }

        public int Width
        {
            get; private set; 
        }

        int[] _pixels = null;

        [Obsolete("Use PixelData with its extension methods.")]
        public int[] Pixels
        {
            get
            {
                if (_optimizedBitmapHolder)
                    return _pixelData.ToIntPixelArray();

                return _pixels;
            }
        }

        byte[] _pixelData = null;

        public byte[] PixelData
        {
            get
            {
                if (!_optimizedBitmapHolder)
                    return _pixels.ToBytePixelArray();

                return _pixelData;
            }
        }

        public void SetPixel(int x, int y, int color)
        {
            if (_optimizedBitmapHolder)
            {
                int pixelPos = (y * Width + x);
                PixelData.SetPixel(pixelPos, color);
            }
            else
            {
                if (x < Width && y < Height)
                    _pixels[y * Width + x] = color;
            }
        }

        public void SetPixel(int x, int y, Color color)
        {
            SetPixel(x, y, color.ToInt());
        }

		public void FreePixels()
		{
			_pixels = null;
            _pixelData = null;
		}
    }

    public static class IBitmapExtensions
    {
        public static BitmapHolder ToNative(this IBitmap bitmap)
        {
            return (BitmapHolder)bitmap;
        }
    }
}
