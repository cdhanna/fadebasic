namespace DAP;

public static class DAPEnv
{
    public static readonly string FadeProgramFile = Environment.GetEnvironmentVariable("FADE_PROGRAM");
    public static readonly string LogPath = Environment.GetEnvironmentVariable("FADE_DAP_LOG_PATH");
    public static readonly bool WaitForDebugger = Environment.GetEnvironmentVariable("FADE_WAIT_FOR_DEBUG") == "true";

}