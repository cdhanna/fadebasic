namespace FadeBasic.Inference
{
    
    /*
     *
     * do a second pass?
     *
     * store state on the scope that says, "in variable mode, or function mode"
     * 
     * look at the return statements of a function
     * if one of them is a func-ref, then evaluate the type of that function
     *  if the execution comes back to the original function, then mark it as UNSET
     * 
     *
     *
     *
     *
     *
     *
     *
     *
     *
     *
     * 
     */
    
    public class Rule
    {
        public long left, right;
        public Constraint op;
    }
    
    public enum Constraint
    {
        UNRESOLVED,
        ASSIGNABLE_TO,
        IS
    }
    
    public class InferenceEngine
    {
        
    }
}