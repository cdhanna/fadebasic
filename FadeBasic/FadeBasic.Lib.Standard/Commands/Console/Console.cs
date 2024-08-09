using System;
using FadeBasic.Lib.Standard.Util;
using FadeBasic.SourceGenerators;

namespace FadeBasic.Lib.Standard
{
    public partial class ConsoleCommands
    {
        [FadeBasicCommand("wait key")]
        public static void WaitKey()
        {
            var _ = System.Console.ReadKey();
        }

        [FadeBasicCommand("print")]
        public static void PrintLines(params object[] elements)
        {
            foreach (var element in elements)
            {
                System.Console.WriteLine(element);
            }
        }
        
        [FadeBasicCommand("write")]
        public static void Print(params object[] elements)
        {
            foreach (var element in elements)
            {
                System.Console.Write(element);
            }
        }

        [FadeBasicCommand("input")]
        public static void Input(string prompt, ref string output)
        {
            Print(prompt);
            output = System.Console.ReadLine();
        }
        
        
        [FadeBasicCommand("input")]
        public static void Input(ref string output)
        {
            output = System.Console.ReadLine();
        }
        
        
        [FadeBasicCommand("set cursor")]
        public static void SetCursor(int x, int y)
        {
            System.Console.SetCursorPosition(x, y);
        }
        
        [FadeBasicCommand("ink console")]
        public static void Ink(int foreground, int background)
        {
            ColorUtil.UnpackColor(foreground, out var fColor);
            ColorUtil.UnpackColor(background, out var bColor);
            
            // find the closest ConsoleColor...
            Console.ForegroundColor = fColor.ToNearestConsoleColor();
            Console.BackgroundColor = bColor.ToNearestConsoleColor();
        }

        [FadeBasicCommand("cls")]
        public static void Clear()
        {
            Console.Clear();
            
        }
        
        [FadeBasicCommand("cls")]
        public static void Clear(int bgColor)
        {
            var existing = Console.BackgroundColor;
            ColorUtil.UnpackColor(bgColor, out var color);
            var next = color.ToNearestConsoleColor();

            Console.BackgroundColor = next;
            Console.Clear();
            var spaces = new string(' ', ConsoleWidth());
            for (var i = 0; i < ConsoleHeight(); i++)
            {
                Console.WriteLine(spaces);
            }

            Console.BackgroundColor = existing;
            Console.SetCursorPosition(0,0);
        }

        [FadeBasicCommand("console width")]
        public static int ConsoleWidth()
        {
            return Console.LargestWindowWidth;
        }
        
        [FadeBasicCommand("console height")]
        public static int ConsoleHeight()
        {
            return Console.LargestWindowHeight;
        }
    }
}