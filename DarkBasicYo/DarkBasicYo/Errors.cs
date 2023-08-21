namespace DarkBasicYo
{
    public class TokenRange
    {
        public Token start;
        public Token end;
    }
    
    public class ProgramError
    {
        public TokenRange location;
        public string message;
        public ErrorCode code;
    }

    public enum ErrorCode
    {
        LexerError
    }
    
    
    /*
     * Some errors to catch... 
     * - You cannot assign something to a function with no return type.
     * - type safety?
     * - argument counts
     */
    
    
    // public class 
}