using System;
using System.Collections.Generic;
using System.Threading;
using FadeBasic.Lib.Standard.Util;
using FadeBasic.SourceGenerators;
using FadeBasic.Virtual;

namespace FadeBasic.Lib.Standard
{
    public partial class ConsoleCommands
    {
        /// <summary>
        /// stops the execution until <i>any</i> key has been pressed
        /// </summary>
        [FadeBasicCommand("wait key")]
        public static void WaitKey()
        {
            var _ = System.Console.ReadKey();
        }

        /// <summary>
        /// print the given arguments to the console.
        ///
        /// <para>
        /// Each argument will be printed on its own line
        /// </para>
        /// </summary>
        /// <param name="elements">The elements to be printed. Each element will be printed on its own line</param>
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
        
        
        [FadeBasicCommand("beep")]
        public static void Beep()
        {
            Console.Beep();
        }
        
        /// <summary>
        /// 
        /// </summary>
        /// <param name="freq">frequency of the beep in HTz, should be between <c>37</c> to <c>32767</c></param>
        /// <param name="duration">duration of the beep in milliseconds</param>
        [FadeBasicCommand("beep")]
        public static void Beep(int freq, int duration)
        {
            Console.Beep(freq, duration);
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

        private static Dictionary<VirtualMachine, int> syncData = new Dictionary<VirtualMachine, int>();
        private static Dictionary<VirtualMachine, DateTimeOffset> syncTimes = new Dictionary<VirtualMachine, DateTimeOffset>();
        
        [FadeBasicCommand("sync on")]
        public static void SyncOn([FromVm]VirtualMachine vm)
        {
            if (syncData.ContainsKey(vm)) return;
            syncData[vm] = 60;
        }
        
        [FadeBasicCommand("sync off")]
        public static void SyncOff([FromVm]VirtualMachine vm)
        {
            syncData.Remove(vm);
        }

        
        [FadeBasicCommand("sync rate")]
        public static void SyncRate([FromVm]VirtualMachine vm, int rate)
        {
            SyncOn(vm);
            syncData[vm] = rate;
        }
        
        [FadeBasicCommand("sync")]
        public static void Sync([FromVm]VirtualMachine vm)
        {
            if (!syncData.TryGetValue(vm, out var rate))
            {
                // if sync is not enabled, then do nothing
                return; 
            }
            
            if (!syncTimes.TryGetValue(vm, out var lastTime))
            {
                syncTimes[vm] = DateTimeOffset.Now;
            }
            else
            {
                var delta = DateTimeOffset.Now - lastTime;
                var targetDelta = TimeSpan.FromSeconds(1.0 / rate);
                var waitTime = targetDelta - delta;
                if (waitTime.TotalMilliseconds > 0)
                {
                    Thread.Sleep(waitTime);
                }
                syncTimes[vm] = DateTimeOffset.Now;
            }
        }
        
        /// <summary>
        /// Set the console cursor to a specific x and y location on the terminal
        /// </summary>
        /// <example>
        /// <code>
        ///     SET CURSOR 1, 1
        /// </code>
        /// </example>
        /// <param name="x">The x coordinate to place the cursor. A value of <c>0</c> implies the left side of the terminal.</param>
        /// <param name="y">The y coordinate to place the cursor. A value of <c>0</c> implies the bottom of the terminal. </param>
        /// <remarks>
        /// Changing the cursor position may cause flickering on the terminal. 
        /// </remarks>
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

        /// <summary>
        /// Get the width of the current console window
        /// </summary>
        /// <returns>the number of columns in the current console window</returns>
        [FadeBasicCommand("console width")]
        public static int ConsoleWidth()
        {
            return Console.LargestWindowWidth;
        }
        
        /// <summary>
        /// Get the height of the current console window
        /// </summary>
        /// <returns>the number of rows in the current console window</returns>
        [FadeBasicCommand("console height")]
        public static int ConsoleHeight()
        {
            return Console.LargestWindowHeight;
        }
    }
}