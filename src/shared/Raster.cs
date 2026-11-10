//  Raster.cs - буфер пикселей RGBA, в который рисуется график HUD.
//
//  Зачем:  график рисуется НЕ прямоугольниками через GUI (по одному на столбик -
//          дорого), а один раз в этот буфер, и в игру уходит одна текстура.
//  Формат: byte[] RGBA, строка y=0 - НИЖНЯЯ (так хранит Unity-текстура),
//          цвета задаются как 0xRRGGBBAA.
//  Файл не зависит от Unity - проверяется без игры.
using System;

namespace Gaslight
{
    public sealed class Raster
    {
        public readonly int W;
        public readonly int H;
        public readonly byte[] Rgba;

        public Raster(int w, int h)
        {
            W = w; H = h;
            Rgba = new byte[w * h * 4];
        }

        public void Clear(uint c)
        {
            byte r = (byte)(c >> 24), g = (byte)(c >> 16), b = (byte)(c >> 8), a = (byte)c;
            for (int i = 0; i < Rgba.Length; i += 4)
            {
                Rgba[i] = r; Rgba[i + 1] = g; Rgba[i + 2] = b; Rgba[i + 3] = a;
            }
        }

        // Закрашивает прямоугольник (x,y - левый нижний угол). Выходы за края обрезаются.
        public void FillRect(int x, int y, int w, int h, uint c)
        {
            int x0 = Math.Max(0, x), y0 = Math.Max(0, y);
            int x1 = Math.Min(W, x + w), y1 = Math.Min(H, y + h);
            if (x1 <= x0 || y1 <= y0) return;
            byte r = (byte)(c >> 24), g = (byte)(c >> 16), b = (byte)(c >> 8), a = (byte)c;
            for (int yy = y0; yy < y1; yy++)
            {
                int p = (yy * W + x0) * 4;
                for (int xx = x0; xx < x1; xx++)
                {
                    Rgba[p] = r; Rgba[p + 1] = g; Rgba[p + 2] = b; Rgba[p + 3] = a;
                    p += 4;
                }
            }
        }

        public void HLine(int x, int y, int w, uint c)
        {
            FillRect(x, y, w, 1, c);
        }
    }
}
