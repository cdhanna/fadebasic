using FadeBasic.SourceGenerators;

namespace FadeBasic.Commands
{
    // the class must be partial for the FadeBasic source generator to add the interface declaration.  
    public partial class CommandProject
    {
        // fade basic command methods must be static, and tagged with the FadeBasicCommand attribute
        [FadeBasicCommand("sample")]
        public static int Sample(int a)
        {
            return a * 2;
        }
    }
}