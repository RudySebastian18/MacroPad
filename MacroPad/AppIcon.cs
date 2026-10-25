using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace MacroPad
{
    /// <summary>
    /// Dibuja el ícono de MacroPad por código: un cuadrado azul redondeado con una cuadrícula 3x3 de teclas.
    /// Así no hace falta un archivo .ico aparte.
    /// </summary>
    public static class AppIcon
    {
        public static Icon Create()
        {
            using var bitmap = new Bitmap(64, 64);

            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                using (var blue = new SolidBrush(Color.FromArgb(0x2F, 0x6F, 0xED)))
                using (var plate = RoundedRect(new Rectangle(2, 2, 60, 60), 16))
                    g.FillPath(blue, plate);

                // Cuadrícula 3x3 de teclas
                using var white = new SolidBrush(Color.White);
                for (int row = 0; row < 3; row++)
                {
                    for (int col = 0; col < 3; col++)
                    {
                        var key = new Rectangle(11 + col * 16, 11 + row * 16, 12, 12);
                        using var path = RoundedRect(key, 4);
                        g.FillPath(white, path);
                    }
                }
            }

            IntPtr handle = bitmap.GetHicon();
            return Icon.FromHandle(handle);
        }

        /// <summary>Convierte el ícono a un formato que entiende WPF (para Window.Icon).</summary>
        public static System.Windows.Media.ImageSource ToImageSource(Icon icon)
        {
            return System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                System.Windows.Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
