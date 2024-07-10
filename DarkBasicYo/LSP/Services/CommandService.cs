using System.Runtime.Loader;

namespace LSP.Services;

public class CommandService
{
    public void LoadDll()
    {
        var context = new AssemblyLoadContext("dll-scope", true);
        // context.un
    }
}