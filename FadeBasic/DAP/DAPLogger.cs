using Microsoft.Win32.SafeHandles;

namespace DAP;

public interface IDAPLogger
{
    void Log(string msg);
}

public class EmptyLogger : IDAPLogger
{
    public void Log(string msg)
    {
        
    }
}

public class DAPLogger : IDisposable, IDAPLogger
{
    public static bool TryCreateLogger( out IDAPLogger logger)
    {
        var emptyLogger = logger = new EmptyLogger();

        if (string.IsNullOrEmpty(DAPEnv.LogPath))
        {
            return false;
        }

        if (Path.IsPathFullyQualified(DAPEnv.LogPath))
        {
            try
            {
                logger = new DAPLogger(DAPEnv.LogPath);
                return true;
            }
            catch
            {
                logger = emptyLogger;
                return false;
            }
        }
        else
        {
            var directory = Path.GetDirectoryName(DAPEnv.FadeProgramFile);
            var newPath = Path.Combine(directory, DAPEnv.LogPath);
            try
            {
                logger = new DAPLogger(newPath);
                return true;
            }
            catch
            {
                logger = emptyLogger;
                return false;
            }
        }
    }
    
    
    private readonly string _logPath;
    private FileStream _fs;
    private StreamWriter _sw;

    public DAPLogger(string logPath)
    {
        _logPath = logPath;

        try
        {
            _fs = new FileStream(logPath, FileMode.Append, FileAccess.Write);
            _sw = new StreamWriter(_fs)
            {
                AutoFlush = true,
            };
        }
        catch (Exception ex)
        {
            throw;
        }
    }
    
    public void Log(string msg)
    {
        _sw.WriteLine(msg);
    }

    public void Dispose()
    {
        _fs.Dispose();
        _sw.Dispose();
    }
}