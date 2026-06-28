// This is a generated file. Do not edit directly.

using System;
using System.Collections.Generic;
using FadeBasic;
using FadeBasic.Launch;
using FadeBasic.Virtual;

public partial class GeneratedFade : ITestLaunchable
{
    
    public static int Main(string[] args)
    {
        return Launcher.Main<GeneratedFade>(args);
    }


    // this byteCode represents a fully compiled program
    public byte[] Bytecode => _byteCode;

    // this table represents the baked commands available within the program
    public CommandCollection CommandCollection => _collection;

    public DebugData DebugData => _debugData;

    public IReadOnlyList<TestManifestEntry> TestManifest => _testManifest;

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
    protected const string encodedByteCode = "NgAAAAEL////f////38JCQEAAQAAAAEAAwAAAA4BAB0AAAA/EQEALQAAACUVNzcBAP///38VeyJ0eXBlcyI6e30sImZ1bmN0aW9ucyI6e30sInN0cmluZ3MiOlt7InZhbHVlIjoiaGVsbG8gd29ybGQiLCJpbmRleFJlZmVyZW5jZXMiOls0XX1dLCJtYXhSZWdpc3RlckFkZHJlc3NTZXJpYWxpemVyIjoiMSJ9";
    #endregion

    #region testManifest
    protected IReadOnlyList<TestManifestEntry> _testManifest = LaunchUtil.UnpackTestManifest(encodedTestManifest);
    protected const string encodedTestManifest = "eyJlbnRyaWVzIjpbXX0=";
    #endregion
}
