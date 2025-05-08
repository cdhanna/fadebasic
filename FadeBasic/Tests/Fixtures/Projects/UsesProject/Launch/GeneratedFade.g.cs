// This is a generated file. Do not edit directly.

using System;
using FadeBasic;
using FadeBasic.Launch;
using FadeBasic.Virtual;

public class GeneratedFade : ILaunchable
{
    
    public static void Main(string[] args)
    {
        Launcher.Run<GeneratedFade>();
    }


    // this byteCode represents a fully compiled program
    public byte[] Bytecode => _byteCode;

    // this table represents the baked commands available within the program
    public CommandCollection CommandCollection => _collection;

    public DebugData DebugData => _debugData;

    #region method table
    private static readonly CommandCollection _collection = new CommandCollection(
        new FadeCommandsViaNuget.MyCommands()
    );
    #endregion

    #region debugData
    protected DebugData _debugData = LaunchUtil.UnpackDebugData(encodedDebugData);
    protected const string encodedDebugData = "eyJpbnNUb1ZhcmlhYmxlIjp7IjIwIjp7Imluc0luZGV4IjoyMCwibmFtZSI6IngiLCJpc1B0ciI6MH19LCJzdGF0ZW1lbnRUb2tlbnMiOlt7Imluc0luZGV4Ijo0LCJ0b2tlbiI6eyJsaW5lTnVtYmVyIjowLCJjaGFyTnVtYmVyIjowLCJyYXciOiJ4IiwiY2FzZUluc2Vuc2l0aXZlUmF3IjoieCJ9LCJpc0NvbXB1dGVkIjowfSx7Imluc0luZGV4IjoyMSwidG9rZW4iOnsibGluZU51bWJlciI6MSwiY2hhck51bWJlciI6MCwicmF3IjoidG9hc3QiLCJjYXNlSW5zZW5zaXRpdmVSYXciOiJ0b2FzdCJ9LCJpc0NvbXB1dGVkIjowfSx7Imluc0luZGV4IjozOCwidG9rZW4iOnsibGluZU51bWJlciI6MywiY2hhck51bWJlciI6MCwicmF3IjoicHJpbnQiLCJjYXNlSW5zZW5zaXRpdmVSYXciOiJwcmludCJ9LCJpc0NvbXB1dGVkIjowfV0sImluc1RvRnVuY3Rpb24iOnt9fQ==";
    #endregion

    #region bytecode
    protected byte[] _byteCode = LaunchUtil.Unpack64(encodedByteCode);
    protected const string encodedByteCode = "PwAAAAEABQAAAAkABwAAAAAAAAAAAQoAAAAAAAAAAAEAAQAAAA4IAAAAAAAAAAAJAAEAAAAAAA4BAP///38VeyJ0eXBlcyI6e30sImZ1bmN0aW9ucyI6e30sInN0cmluZ3MiOltdLCJtYXhSZWdpc3RlckFkZHJlc3NTZXJpYWxpemVyIjoiMiJ9";
    #endregion
}
