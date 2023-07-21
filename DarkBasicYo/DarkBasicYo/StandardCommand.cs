namespace DarkBasicYo
{
    public static class StandardCommands
    {
        // public static readonly CommandDescriptor PrintCommand =
        //     new CommandDescriptor("print", new ArgDescriptor("data", LiteralType.Any));
        //
        // public static readonly CommandDescriptor WaitKey =
        //     new CommandDescriptor("wait key");

        public static readonly CommandCollection LimitedCommands = new CommandCollection(typeof(StandardCommands));


        [CommandName("print")]
        public static void Print(int x)
        {
            
        }
        
        [CommandName("wait key")]
        public static void WaitKey()
        {
            
        }
    }
}