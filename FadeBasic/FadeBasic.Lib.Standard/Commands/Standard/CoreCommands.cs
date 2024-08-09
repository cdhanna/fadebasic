using System;
using FadeBasic.SourceGenerators;

namespace FadeBasic.Lib.Standard
{
    public partial class StandardCommands
    {
        private static Random _rand = new Random();
        private static DateTimeOffset _started = DateTimeOffset.Now;
        static StandardCommands()
        {
            
        }
        
        [FadeBasicCommand("randomize")]
        public static void RandomSeed(int seed)
        {
            _rand = new Random(seed);
        }
        
        [FadeBasicCommand("rnd")]
        public static int Random(int max)
        {
            return _rand.Next(max);
        }
        
        [FadeBasicCommand("timer")]
        public static int Timer()
        {
            var now = DateTimeOffset.Now;
            var delta = now - _started;
            return (int)delta.TotalMilliseconds;
        }
        
        [FadeBasicCommand("inc")]
        public static void Increment(ref int value, int amount = 1)
        {
            value += amount;
        }
                
        [FadeBasicCommand("dec")]
        public static void Decrement(ref int value, int amount = 1)
        {
            value -= amount;
        }
    }
}