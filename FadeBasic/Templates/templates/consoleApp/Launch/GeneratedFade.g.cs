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
        new FadeBasic.Lib.Standard.ConsoleCommands(), new FadeBasic.Lib.Standard.StandardCommands()
    );
    #endregion

    #region debugData
    protected DebugData _debugData = LaunchUtil.UnpackDebugData(encodedDebugData);
    protected const string encodedDebugData = "eyJpbnNUb1ZhcmlhYmxlIjp7fSwic3RhdGVtZW50VG9rZW5zIjpbeyJpbnNJbmRleCI6NCwidG9rZW4iOnsibGluZU51bWJlciI6NCwiY2hhck51bWJlciI6MCwicmF3IjoicHJpbnQiLCJjYXNlSW5zZW5zaXRpdmVSYXciOiJwcmludCJ9LCJpc0NvbXB1dGVkIjowfV0sImluc1RvRnVuY3Rpb24iOnt9fQ==";
    #endregion

    #region bytecode
    protected byte[] _byteCode = LaunchUtil.Unpack64(encodedByteCode);
    protected const string encodedByteCode = "IAAAAAEA////fwkJAQABAAAAAQABAAAADgEA////fxV7InR5cGVzIjp7fSwiZnVuY3Rpb25zIjp7fSwic3RyaW5ncyI6W3sidmFsdWUiOiJoZWxsbyB3b3JsZCIsImluZGV4UmVmZXJlbmNlcyI6WzRdfV19";
    #endregion
}
