

using System.Collections.Generic;
using DarkBasicYo.Virtual;

namespace DarkBasicYo
{
    public class StandardCommands
    {
        public static CommandCollection LimitedCommands => new CommandCollection(new DarkBasicCommands());
    }

    
    public class Example
    {
        
        public static void Test()
        {
            var commands = new DarkBasicCommands();
            
            // var n = new AnaExample.AnaAnaAna();
            // DarkBasicCommandsMetaData.COMMANDS_JSON;
            var x = 132;

            // var collection = new CommandCollection(commands);
            // commands.Commands[0].executor(null);
            // commands.Count
            // GeneratedDarkBasicCommands.Call2_Ana(null);
            // DarkBasicCommandUtil.Autogenerated.GeneratedDarkBasicCommands.Call2_Ana(null);

        }
        
    }
}