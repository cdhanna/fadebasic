using System;
using System.IO;

namespace FadeBasic.Virtual
{
    public interface IDebugLogger
    {
        void Log(string msg);
    }

    public class EmptyDebugLogger : IDebugLogger
    {
        public void Log(string msg)
        {
        }
    }
    
    public class DebugLogger : IDisposable, IDebugLogger
    {
        private FileStream _fs;
        private StreamWriter _sw;

        public DebugLogger(string logPath)
        {
            _fs = new FileStream(logPath, FileMode.Append, FileAccess.Write);
            _sw = new StreamWriter(_fs)
            {
                AutoFlush = true
            };
        }
        
        public void Log(string msg)
        {
            _sw.WriteLine(msg);
        }

        public void Dispose()
        {
            _sw.Dispose();
            _fs.Dispose();
        }
    }
}