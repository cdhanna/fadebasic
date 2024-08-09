using System;
using System.Collections.Generic;
using FadeBasic.SourceGenerators;

namespace FadeBasic.Lib.Standard
{
    public partial class ConsoleCommands
    {
        private static ConsoleKeyInfo _available;
        private static bool _hasKey;

        private static Queue<KeyLog> _keyLog = new Queue<KeyLog>();
        private static Queue<KeyLog> _keyLogBackBuffer = new Queue<KeyLog>();
        
        public struct KeyLog
        {
            public ConsoleKeyInfo key;
            public DateTimeOffset time;
        }
        
        // [FadeBasicCommand("inkey$")]
        // public static string InKey()
        // {
        //     if (!TryGetKey(out var info)) return "";
        //     return info.Key.ToString();
        // }
        //
        
        
        [FadeBasicCommand("hide cursor")]
        public static void HideCursor()
        {
            Console.CursorVisible = false;
        }
        
        [FadeBasicCommand("leftkey")]
        public static int LeftKey()
        {
            return IsKeyDown(ConsoleKey.LeftArrow) ? 1 : 0;
        }
        
        [FadeBasicCommand("rightkey")]
        public static int RightKey()
        {
            return IsKeyDown(ConsoleKey.RightArrow) ? 1 : 0;
        }
        
        [FadeBasicCommand("upkey")]
        public static int UpKey()
        {
            return IsKeyDown(ConsoleKey.UpArrow) ? 1 : 0;
        }    
        
        [FadeBasicCommand("downkey")]
        public static int DownKey()
        {
            return IsKeyDown(ConsoleKey.DownArrow) ? 1 : 0;
        }
        
        [FadeBasicCommand("returnkey")]
        public static int ReturnKey()
        {
            return IsKeyDown(ConsoleKey.Enter) ? 1 : 0;
        }
        
        [FadeBasicCommand("spacekey")]
        public static int SpaceKey()
        {
            return IsKeyDown(ConsoleKey.Spacebar) ? 1 : 0;
        }
        
        [FadeBasicCommand("escapekey")]
        public static int EscapeKey()
        {
            return IsKeyDown(ConsoleKey.Escape) ? 1 : 0;
        }
        
        public static bool IsKeyDown(ConsoleKey key)
        {
            AcceptKeys();
            return TryConsomeAllKeys(key);
            // if (!TryGetKey(out var info)) return false;
            // var isMatch = info.Key == key;
            // if (isMatch)
            // {
            //     
            // }
            // return info.Key == key;
        }

        public static bool TryConsomeAllKeys(ConsoleKey targetKey)
        {
            _keyLogBackBuffer.Clear();
            // discard old keys...
            var found = false;
            while (_keyLog.Count > 0)
            {
                var key = _keyLog.Dequeue();
                if (key.key.Key == targetKey)
                {
                    found = true;
                    // do not re-commit this key to the next buffer.
                }
                else
                {
                    _keyLogBackBuffer.Enqueue(key);
                }
            }
            (_keyLogBackBuffer, _keyLog) = (_keyLog, _keyLogBackBuffer);

            return found;
        }

        public static void AcceptKeys()
        {
            _keyLogBackBuffer.Clear();
            var now = DateTimeOffset.Now;
            
            // discard old keys...
            while (_keyLog.Count > 0)
            {
                var key = _keyLog.Dequeue();
                var age = now - key.time;
                if (age > TimeSpan.FromMilliseconds(10))
                {
                    continue; // discard this old key-press.
                }
                _keyLogBackBuffer.Enqueue(key);
            }

            // swap
            (_keyLogBackBuffer, _keyLog) = (_keyLog, _keyLogBackBuffer);
            
            // collect new keys
            if (!Console.KeyAvailable) return; // nothing to be done
            var info = Console.ReadKey(true);
            _keyLog.Enqueue(new KeyLog
            {
                time = now,
                key = info
            });
        }

        // public static bool TryGetKey(out ConsoleKeyInfo info)
        // {
        //     info = default;
        //
        //     _keyLogBackBuffer.Clear();
        //     var now = DateTimeOffset.Now;
        //     while (_keyLog.Count > 0)
        //     {
        //         var key = _keyLog.Dequeue();
        //         var age = now - key.time;
        //         if (age > TimeSpan.FromMilliseconds(10))
        //         {
        //             continue; // discard this old key-press.
        //         }
        //         
        //     }
        //
        //     if (!Console.KeyAvailable) return false;
        //     
        //     info = Console.ReadKey(true);
        //     _keyLog.Enqueue(new KeyLog
        //     {
        //         time = now,
        //         key = info
        //     });
        //     
        //     return true;
        // }

        public static void ClearInput()
        {
            // var buffer = Console.In;
            // while (Console.KeyAvailable)
            // {
            //     var _ = buffer.Read();
            // }
        }
    }
}