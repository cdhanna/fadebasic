using System;
using System.Numerics;

namespace FadeBasic.Lib.Standard.Util
{
    public static class ColorUtil
    {
        public struct Color
        {
            public byte r, g, b, a;

            public Color(byte r, byte g, byte b, byte a=255)
            {
                this.r = r;
                this.g = g;
                this.b = b;
                this.a = a;
            }

            public static Vector4 operator -(Color a, Color b)
                => new Vector4(a.r - b.r, a.g - b.g, a.b - b.b, a.a - b.a);

            public int SquaredMag()
            {
                return (r * r + g * g + b * b + a * a);
            }
            public static readonly Color Black = new Color(0, 0, 0);
            public static readonly Color DarkBlue = new Color(0, 0, 128);
            public static readonly Color DarkGreen = new Color(0, 128, 0);
            public static readonly Color DarkCyan = new Color(0, 128, 128);
            public static readonly Color DarkRed = new Color(128, 0,0);
            public static readonly Color DarkMagenta = new Color(128, 0,128);
            public static readonly Color DarkYellow = new Color(128, 128,0);
            public static readonly Color Gray = new Color(200, 200,200);
            public static readonly Color DarkGray = new Color(100, 100,100);
            public static readonly Color Blue = new Color(0, 0,255);
            public static readonly Color Green = new Color(0, 255,0);
            public static readonly Color Cyan = new Color(0, 255,255);
            public static readonly Color Magenta = new Color(255, 0,255);
            public static readonly Color Yellow = new Color(255, 255,0);
            public static readonly Color White = new Color(255, 255,255);
            public static readonly Color Red = new Color(255, 0, 0);
        }
        
        public static void PackColor(byte r, byte g, byte b, byte a, out int colorCode)
        {
            var bytes = new byte[]
            {
                r, g, b, a
            };
            colorCode = BitConverter.ToInt32(bytes, 0);
        }
        
        public static void UnpackColor(int colorCode, out byte r, out byte g, out byte b, out byte a)
        {
            var bytes = BitConverter.GetBytes(colorCode);
            r = bytes[0];
            g = bytes[1];
            b = bytes[2];
            a = bytes[3];
        }
        public static void UnpackColor(int colorCode, out Color color)
        {
            UnpackColor(colorCode, out color.r, out color.g, out color.b, out color.a);
        }

        private static Color[] ConsoleColors = new Color[]
        {
            Color.Black,
            Color.White,
            Color.Magenta,
            Color.DarkMagenta,
            Color.Yellow,
            Color.DarkYellow,
            Color.Cyan,
            Color.DarkCyan,
            Color.Blue,
            Color.DarkBlue,
            Color.Gray,
            Color.DarkGray,
            Color.Green,
            Color.DarkGreen,
            Color.Red,
            Color.DarkRed,
            Color.Red,
        };
        private static ConsoleColor[] ConsoleColorsEnums = new ConsoleColor[]
        {
            ConsoleColor.Black,
            ConsoleColor.White,
            ConsoleColor.Magenta,
            ConsoleColor.DarkMagenta,
            ConsoleColor.Yellow,
            ConsoleColor.DarkYellow,
            ConsoleColor.Cyan,
            ConsoleColor.DarkCyan,
            ConsoleColor.Blue,
            ConsoleColor.DarkBlue,
            ConsoleColor.Gray,
            ConsoleColor.DarkGray,
            ConsoleColor.Green,
            ConsoleColor.DarkGreen,
            ConsoleColor.Red,
            ConsoleColor.DarkRed,
            ConsoleColor.Red,
        };

        public static ConsoleColor ToNearestConsoleColor(this Color color)
        {
            var smallestMag = float.MaxValue;
            var smallestIndex = 0;
            for (var i = 0; i < ConsoleColors.Length; i++)
            {
                var diff = ConsoleColors[i] - color;
                var mag = diff.LengthSquared();
                if (mag < smallestMag)
                {
                    smallestMag = mag;
                    smallestIndex = i;
                }
            }

            return ConsoleColorsEnums[smallestIndex];
        }
    }
}