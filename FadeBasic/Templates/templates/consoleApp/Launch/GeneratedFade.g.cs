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
    protected const string encodedDebugData = "eyJpbnNUb1ZhcmlhYmxlIjp7fSJzdGF0ZW1lbnRUb2tlbnMiOlt7Imluc0luZGV4Ijo0LCJ0b2tlbiI6eyJsaW5lTnVtYmVyIjo0LCJjaGFyTnVtYmVyIjowLCJyYXciOiJwcmludCIsImNhc2VJbnNlbnNpdGl2ZVJhdyI6InByaW50In0sImlzQ29tcHV0ZWQiOjB9XSJpbnNUb0Z1bmN0aW9uIjp7fX0=";
    #endregion

    #region bytecode
    protected byte[] _byteCode = LaunchUtil.Unpack64(encodedByteCode);
    protected const string encodedByteCode = "bAAAABIAaAAAABIAZQAAABIAbAAAABIAbAAAABIAbwAAABIAIAAAABIAdwAAABIAbwAAABIAcgAAABIAbAAAABIAZAAAAAEALAAAABEtAAkAAAAAChMJCQEAAQAAAAEAAQAAAA4BAP///38VeyJ0eXBlcyI6e319";
    #endregion
}
