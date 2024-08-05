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

    #region method table
    private static readonly CommandCollection _collection = new CommandCollection(
        new FadeBasic.FadeBasicCommands()
    );
    #endregion

    #region bytecode
    protected byte[] _byteCode = LaunchUtil.Unpack64(encodedByteCode);
    protected const string encodedByteCode = "EgBoAAAAEgBlAAAAEgBsAAAAEgBsAAAAEgBvAAAAEgAgAAAAEgB3AAAAEgBvAAAAEgByAAAAEgBsAAAAEgBkAAAAAQAsAAAAEQoTCQkBAAAAAAAO";
    #endregion
}
