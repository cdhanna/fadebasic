using System;
using System.Threading;
using FadeBasic.Lib.Standard.Util;
using FadeBasic.SourceGenerators;

namespace FadeBasic.Lib.Standard
{
    public partial class StandardCommands
    {
        [FadeBasicCommand("rgb")]
        public static int Rgb(byte r, byte g, byte b, byte a=255)
        {
            ColorUtil.PackColor(r,g,b,a, out var color);
            return color;
        }

        [FadeBasicCommand("wait ms")]
        public static void Wait(int milliseconds)
        {
            Thread.Sleep(milliseconds);
        }

    }
}