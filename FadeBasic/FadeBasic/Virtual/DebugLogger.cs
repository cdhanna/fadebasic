using System;
using System.IO;

namespace FadeBasic.Virtual
{
    public enum LogLevel
    {
        DEBUG = 1,
        INFO = 2,
        ERROR = 3
    }
    
    public interface IDebugLogger
    {
        public LogLevel Level { get; set; }
        void Emit(LogLevel level, string msg);
    }
    
    public static class IDebugLoggerExtensions
    {
        public static void Info(this IDebugLogger logger, string msg) => logger.Emit(LogLevel.INFO, msg);
        public static void Debug(this IDebugLogger logger, string msg) => logger.Emit(LogLevel.DEBUG, msg);
        public static void Log(this IDebugLogger logger, string msg) => logger.Emit(LogLevel.INFO, msg);
        public static void Error(this IDebugLogger logger, string msg) => logger.Emit(LogLevel.ERROR, msg);
    }
    
    

    public class EmptyDebugLogger : IDebugLogger
    {
        public LogLevel Level { get; set; }

        public void Emit(LogLevel level, string msg)
        {
        }
    }
    
    public class DebugLogger : IDisposable, IDebugLogger
    {
        private FileStream _fs;
        private StreamWriter _sw;
        public LogLevel Level { get; set; }

        public DebugLogger(string logPath)
        {
            _fs = new FileStream(logPath, FileMode.Append, FileAccess.Write);
            _sw = new StreamWriter(_fs)
            {
                AutoFlush = true
            };
        }
        
        public void Emit(LogLevel level, string msg)
        {
            if (level >= Level)
            {
                var format = $"[{level}] {msg}";
                _sw.WriteLine(format);
            }
        }

        public void Dispose()
        {
            _sw.Dispose();
            _fs.Dispose();
        }
    }
}