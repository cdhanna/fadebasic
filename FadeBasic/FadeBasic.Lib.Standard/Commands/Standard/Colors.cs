using System;
using System.Threading;
using FadeBasic.Lib.Standard.Util;
using FadeBasic.SourceGenerators;

namespace FadeBasic.Lib.Standard
{
    public partial class StandardCommands
    {
        /// <summary>
        /// Creates a color with values for red, green, blue, and optionally alpha.
        /// Each value should be between 0 and 255. 
        /// </summary>
        /// <param name="r">the red channel of the color.</param>
        /// <param name="g">the green channel of the color. </param>
        /// <param name="b">the blue channel of the color. </param>
        /// <param name="a">the alpha channel of the color. By default, this will be 255, so it is fully opaque. </param>
        /// <returns>A single integer representing the color</returns>
        /// <remarks>
        /// A few common color codes are,
        ///
        /// <list>
        ///     <item> Red - (255, 0, 0) </item>
        ///     <item> Salmon - (255, 128, 128) </item>
        ///     <item> White - (255, 255, 255) </item>
        /// </list>
        ///
        /// <para>
        /// The resulting integer is just a byte packed version of the four strings. It may be negative. 
        /// </para>
        /// </remarks>
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