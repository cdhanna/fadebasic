

using System.Collections.Generic;
using FadeBasic.Virtual;

namespace FadeBasic
{
    public class StandardCommands
    {
        public static CommandCollection LimitedCommands => new CommandCollection(new FadeBasicCommands());
    }

    
    public class Example
    {
        
        public static void Test()
        {
            var commands = new FadeBasicCommands();
            var md = FadeBasicCommandsMetaData.COMMANDS_JSON;
            // var n = new AnaExample.AnaAnaAna();
            // FadeBasicCommandsMetaData.COMMANDS_JSON;
            var x = 134;
            // var collection = new CommandCollection(commands);
            // commands.Commands[0].executor(null);
            // commands.Count
            // GeneratedFadeBasicCommands.Call2_Ana(null);
            // FadeBasicCommandUtil.Autogenerated.GeneratedFadeBasicCommands.Call2_Ana(null);

        }
        
    }
}