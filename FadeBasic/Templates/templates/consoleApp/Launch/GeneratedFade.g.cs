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
    protected const string encodedDebugData = "eyJwb2ludHMiOlt7InJhbmdlIjp7InN0YXJ0VG9rZW4iOnsiaW5zSW5kZXgiOjAsInRva2VuIjp7ImxpbmVOdW1iZXIiOjQsImNoYXJOdW1iZXIiOjAsInJhdyI6InByaW50IiwiY2FzZUluc2Vuc2l0aXZlUmF3IjoicHJpbnQifX0sInN0b3BUb2tlbiI6eyJpbnNJbmRleCI6OTAsInRva2VuIjp7ImxpbmVOdW1iZXIiOjUsImNoYXJOdW1iZXIiOjAsInJhdyI6bnVsbCwiY2FzZUluc2Vuc2l0aXZlUmF3IjoiCiJ9fX0sImlubmVyTWFwcyI6W119XSJpbnNUb1ZhcmlhYmxlIjp7fX0=";
    #endregion

    #region bytecode
    protected byte[] _byteCode = LaunchUtil.Unpack64(encodedByteCode);
    protected const string encodedByteCode = "EgBoAAAAEgBlAAAAEgBsAAAAEgBsAAAAEgBvAAAAEgAgAAAAEgB3AAAAEgBvAAAAEgByAAAAEgBsAAAAEgBkAAAAAQAsAAAAEQoTCQkBAAEAAAABAAEAAAAO";
    #endregion
}
