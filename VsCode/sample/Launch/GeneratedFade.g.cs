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
    protected const string encodedDebugData = "eyJpbnNUb1ZhcmlhYmxlIjp7IjIxIjp7Imluc0luZGV4IjoyMSwibmFtZSI6IndpZHRoIiwiaXNQdHIiOjB9LCIzMiI6eyJpbnNJbmRleCI6MzIsIm5hbWUiOiJoZWlnaHQiLCJpc1B0ciI6MH0sIjU5Ijp7Imluc0luZGV4Ijo1OSwibmFtZSI6InNpemUiLCJpc1B0ciI6MH0sIjcyIjp7Imluc0luZGV4Ijo3MiwibmFtZSI6InNpemUiLCJpc1B0ciI6MH0sIjgxIjp7Imluc0luZGV4Ijo4MSwibmFtZSI6ImNlbGxjb3VudCIsImlzUHRyIjowfSwiMTE5Ijp7Imluc0luZGV4IjoxMTksIm5hbWUiOiJwaWVjZXMiLCJpc1B0ciI6MX0sIjE3NyI6eyJpbnNJbmRleCI6MTc3LCJuYW1lIjoibiIsImlzUHRyIjowfSwiMjA0Ijp7Imluc0luZGV4IjoyMDQsIm5hbWUiOiJ4IiwiaXNQdHIiOjB9LCIyNjQiOnsiaW5zSW5kZXgiOjI2NCwibmFtZSI6InkiLCJpc1B0ciI6MH0sIjM0NCI6eyJpbnNJbmRleCI6MzQ0LCJuYW1lIjoiaW5kZXgiLCJpc1B0ciI6MH0sIjM3NiI6eyJpbnNJbmRleCI6Mzc2LCJuYW1lIjoidmFsdWUiLCJpc1B0ciI6MH0sIjM4OSI6eyJpbnNJbmRleCI6Mzg5LCJuYW1lIjoidGV4dCQiLCJpc1B0ciI6MH0sIjQxNyI6eyJpbnNJbmRleCI6NDE3LCJuYW1lIjoieSIsImlzUHRyIjowfSwiNDM3Ijp7Imluc0luZGV4Ijo0MzcsIm5hbWUiOiJ4IiwiaXNQdHIiOjB9LCI0NTQiOnsiaW5zSW5kZXgiOjQ1NCwibmFtZSI6InkiLCJpc1B0ciI6MH0sIjQ2MCI6eyJpbnNJbmRleCI6NDYwLCJuYW1lIjoieCIsImlzUHRyIjowfSwiNDgxIjp7Imluc0luZGV4Ijo0ODEsIm5hbWUiOiJ2YWx1ZSIsImlzUHRyIjowfSwiNjEwIjp7Imluc0luZGV4Ijo2MTAsIm5hbWUiOiJ4IiwiaXNQdHIiOjB9LCI4MzMiOnsiaW5zSW5kZXgiOjgzMywibmFtZSI6IngiLCJpc1B0ciI6MH19InN0YXRlbWVudFRva2VucyI6W3siaW5zSW5kZXgiOjQsInRva2VuIjp7ImxpbmVOdW1iZXIiOjAsImNoYXJOdW1iZXIiOjAsInJhdyI6IkNMUyIsImNhc2VJbnNlbnNpdGl2ZVJhdyI6ImNscyJ9LCJpc0NvbXB1dGVkIjowfSx7Imluc0luZGV4IjoxMSwidG9rZW4iOnsibGluZU51bWJlciI6MiwiY2hhck51bWJlciI6MCwicmF3Ijoid2lkdGgiLCJjYXNlSW5zZW5zaXRpdmVSYXciOiJ3aWR0aCJ9LCJpc0NvbXB1dGVkIjowfSx7Imluc0luZGV4IjoyMiwidG9rZW4iOnsibGluZU51bWJlciI6MywiY2hhck51bWJlciI6MCwicmF3IjoiaGVpZ2h0IiwiY2FzZUluc2Vuc2l0aXZlUmF3IjoiaGVpZ2h0In0sImlzQ29tcHV0ZWQiOjB9LHsiaW5zSW5kZXgiOjMzLCJ0b2tlbiI6eyJsaW5lTnVtYmVyIjo1LCJjaGFyTnVtYmVyIjowLCJyYXciOiJHTE9CQUwiLCJjYXNlSW5zZW5zaXRpdmVSYXciOiJnbG9iYWwifSwiaXNDb21wdXRlZCI6MH0seyJpbnNJbmRleCI6MzMsInRva2VuIjp7ImxpbmVOdW1iZXIiOjYsImNoYXJOdW1iZXIiOjAsInJhdyI6IklGIiwiY2FzZUluc2Vuc2l0aXZlUmF3IjoiaWYifSwiaXNDb21wdXRlZCI6MH0seyJpbnNJbmRleCI6NTQsInRva2VuIjp7ImxpbmVOdW1iZXIiOjYsImNoYXJOdW1iZXIiOjIzLCJyYXciOiJzaXplIiwiY2FzZUluc2Vuc2l0aXZlUmF3Ijoic2l6ZSJ9LCJpc0NvbXB1dGVkIjowfSx7Imluc0luZGV4Ijo1OSwidG9rZW4iOnsibGluZU51bWJlciI6NiwiY2hhck51bWJlciI6NDgsInJhdyI6ImhlaWdodCIsImNhc2VJbnNlbnNpdGl2ZVJhdyI6ImhlaWdodCJ9LCJpc0NvbXB1dGVkIjoxfSx7Imluc0luZGV4Ijo2NywidG9rZW4iOnsibGluZU51bWJlciI6NiwiY2hhck51bWJlciI6NDEsInJhdyI6InNpemUiLCJjYXNlSW5zZW5zaXRpdmVSYXciOiJzaXplIn0sImlzQ29tcHV0ZWQiOjB9LHsiaW5zSW5kZXgiOjczLCJ0b2tlbiI6eyJsaW5lTnVtYmVyIjo4LCJjaGFyTnVtYmVyIjowLCJyYXciOiJHTE9CQUwiLCJjYXNlSW5zZW5zaXRpdmVSYXciOiJnbG9iYWwifSwiaXNDb21wdXRlZCI6MH0seyJpbnNJbmRleCI6ODIsInRva2VuIjp7ImxpbmVOdW1iZXIiOjksImNoYXJOdW1iZXIiOjAsInJhdyI6IkRJTSIsImNhc2VJbnNlbnNpdGl2ZVJhdyI6ImRpbSJ9LCJpc0NvbXB1dGVkIjowfSx7Imluc0luZGV4IjoxMjAsInRva2VuIjp7ImxpbmVOdW1iZXIiOjExLCJjaGFyTnVtYmVyIjowLCJyYXciOiJQUklOVCIsImNhc2VJbnNlbnNpdGl2ZVJhdyI6InByaW50In0sImlzQ29tcHV0ZWQiOjB9LHsiaW5zSW5kZXgiOjEzNSwidG9rZW4iOnsibGluZU51bWJlciI6MTMsImNoYXJOdW1iZXIiOjAsInJhdyI6ImluaXRCb2FyZCIsImNhc2VJbnNlbnNpdGl2ZVJhdyI6ImluaXRib2FyZCJ9LCJpc0NvbXB1dGVkIjowfSx7Imluc0luZGV4IjoxNDIsInRva2VuIjp7ImxpbmVOdW1iZXIiOjE0LCJjaGFyTnVtYmVyIjowLCJyYXciOiJuIiwiY2FzZUluc2Vuc2l0aXZlUmF3IjoibiJ9LCJpc0NvbXB1dGVkIjowfSx7Imluc0luZGV4IjoxNzgsInRva2VuIjp7ImxpbmVOdW1iZXIiOjE1LCJjaGFyTnVtYmVyIjowLCJyYXciOiJkcmF3Qm9hcmQiLCJjYXNlSW5zZW5zaXRpdmVSYXciOiJkcmF3Ym9hcmQifSwiaXNDb21wdXRlZCI6MH0seyJpbnNJbmRleCI6MTkzLCJ0b2tlbiI6eyJsaW5lTnVtYmVyIjoxNywiY2hhck51bWJlciI6OSwicmF3IjoiZHJhd0JvYXJkIiwiY2FzZUluc2Vuc2l0aXZlUmF3IjoiZHJhd2JvYXJkIn0sImlzQ29tcHV0ZWQiOjF9LHsiaW5zSW5kZXgiOjE5NSwidG9rZW4iOnsibGluZU51bWJlciI6MTgsImNoYXJOdW1iZXIiOjQsInJhdyI6IkZPUiIsImNhc2VJbnNlbnNpdGl2ZVJhdyI6ImZvciJ9LCJpc0NvbXB1dGVkIjowfSx7Imluc0luZGV4IjoyNTUsInRva2VuIjp7ImxpbmVOdW1iZXIiOjE5LCJjaGFyTnVtYmVyIjo4LCJyYXciOiJGT1IiLCJjYXNlSW5zZW5zaXRpdmVSYXciOiJmb3IifSwiaXNDb21wdXRlZCI6MH0seyJpbnNJbmRleCI6MzE1LCJ0b2tlbiI6eyJsaW5lTnVtYmVyIjoyMCwiY2hhck51bWJlciI6MCwicmF3IjoiU0VUIENVUlNPUiIsImNhc2VJbnNlbnNpdGl2ZVJhdyI6InNldCBjdXJzb3IifSwiaXNDb21wdXRlZCI6MH0seyJpbnNJbmRleCI6MzMwLCJ0b2tlbiI6eyJsaW5lTnVtYmVyIjoyMSwiY2hhck51bWJlciI6MCwicmF3IjoiaW5kZXgiLCJjYXNlSW5zZW5zaXRpdmVSYXciOiJpbmRleCJ9LCJpc0NvbXB1dGVkIjowfSx7Imluc0luZGV4IjozNDUsInRva2VuIjp7ImxpbmVOdW1iZXIiOjIyLCJjaGFyTnVtYmVyIjowLCJyYXciOiJ2YWx1ZSIsImNhc2VJbnNlbnNpdGl2ZVJhdyI6InZhbHVlIn0sImlzQ29tcHV0ZWQiOjB9LHsiaW5zSW5kZXgiOjM3NywidG9rZW4iOnsibGluZU51bWJlciI6MjMsImNoYXJOdW1iZXIiOjAsInJhdyI6IlRFWFQkIiwiY2FzZUluc2Vuc2l0aXZlUmF3IjoidGV4dCQifSwiaXNDb21wdXRlZCI6MH0seyJpbnNJbmRleCI6MzkwLCJ0b2tlbiI6eyJsaW5lTnVtYmVyIjoyNCwiY2hhck51bWJlciI6MCwicmF3IjoiV1JJVEUiLCJjYXNlSW5zZW5zaXRpdmVSYXciOiJ3cml0ZSJ9LCJpc0NvbXB1dGVkIjowfSx7Imluc0luZGV4Ijo0NDgsInRva2VuIjp7ImxpbmVOdW1iZXIiOjI5LCJjaGFyTnVtYmVyIjo5LCJyYXciOiJnZXRJbmRleCIsImNhc2VJbnNlbnNpdGl2ZVJhdyI6ImdldGluZGV4In0sImlzQ29tcHV0ZWQiOjF9LHsiaW5zSW5kZXgiOjQ2MiwidG9rZW4iOnsibGluZU51bWJlciI6MzAsImNoYXJOdW1iZXIiOjAsInJhdyI6IkVOREZVTkNUSU9OIiwiY2FzZUluc2Vuc2l0aXZlUmF3IjoiZW5kZnVuY3Rpb24ifSwiaXNDb21wdXRlZCI6MH0seyJpbnNJbmRleCI6NDc1LCJ0b2tlbiI6eyJsaW5lTnVtYmVyIjozMiwiY2hhck51bWJlciI6OSwicmF3IjoiZ2V0UGllY2VUZXh0IiwiY2FzZUluc2Vuc2l0aXZlUmF3IjoiZ2V0cGllY2V0ZXh0In0sImlzQ29tcHV0ZWQiOjF9LHsiaW5zSW5kZXgiOjQ4MywidG9rZW4iOnsibGluZU51bWJlciI6MzMsImNoYXJOdW1iZXIiOjAsInJhdyI6IlNFTEVDVCIsImNhc2VJbnNlbnNpdGl2ZVJhdyI6InNlbGVjdCJ9LCJpc0NvbXB1dGVkIjowfSx7Imluc0luZGV4Ijo1MzQsInRva2VuIjp7ImxpbmVOdW1iZXIiOjM1LCJjaGFyTnVtYmVyIjowLCJyYXciOiJFWElURlVOQ1RJT04iLCJjYXNlSW5zZW5zaXRpdmVSYXciOiJleGl0ZnVuY3Rpb24ifSwiaXNDb21wdXRlZCI6MH0seyJpbnNJbmRleCI6NTUxLCJ0b2tlbiI6eyJsaW5lTnVtYmVyIjozOCwiY2hhck51bWJlciI6MCwicmF3IjoiRVhJVEZVTkNUSU9OIiwiY2FzZUluc2Vuc2l0aXZlUmF3IjoiZXhpdGZ1bmN0aW9uIn0sImlzQ29tcHV0ZWQiOjB9LHsiaW5zSW5kZXgiOjU2OCwidG9rZW4iOnsibGluZU51bWJlciI6NDEsImNoYXJOdW1iZXIiOjAsInJhdyI6IkVYSVRGVU5DVElPTiIsImNhc2VJbnNlbnNpdGl2ZVJhdyI6ImV4aXRmdW5jdGlvbiJ9LCJpc0NvbXB1dGVkIjowfSx7Imluc0luZGV4Ijo1ODYsInRva2VuIjp7ImxpbmVOdW1iZXIiOjQ0LCJjaGFyTnVtYmVyIjowLCJyYXciOiJFTkRGVU5DVElPTiIsImNhc2VJbnNlbnNpdGl2ZVJhdyI6ImVuZGZ1bmN0aW9uIn0sImlzQ29tcHV0ZWQiOjB9LHsiaW5zSW5kZXgiOjU5OSwidG9rZW4iOnsibGluZU51bWJlciI6NDYsImNoYXJOdW1iZXIiOjksInJhdyI6ImluaXRCb2FyZCIsImNhc2VJbnNlbnNpdGl2ZVJhdyI6ImluaXRib2FyZCJ9LCJpc0NvbXB1dGVkIjoxfSx7Imluc0luZGV4Ijo2MDEsInRva2VuIjp7ImxpbmVOdW1iZXIiOjQ3LCJjaGFyTnVtYmVyIjowLCJyYXciOiJGT1IiLCJjYXNlSW5zZW5zaXRpdmVSYXciOiJmb3IifSwiaXNDb21wdXRlZCI6MH0seyJpbnNJbmRleCI6NjYxLCJ0b2tlbiI6eyJsaW5lTnVtYmVyIjo0OCwiY2hhck51bWJlciI6MCwicmF3IjoiSUYiLCJjYXNlSW5zZW5zaXRpdmVSYXciOiJpZiJ9LCJpc0NvbXB1dGVkIjowfSx7Imluc0luZGV4Ijo2OTksInRva2VuIjp7ImxpbmVOdW1iZXIiOjQ4LCJjaGFyTnVtYmVyIjoyMSwicmF3IjoicGllY2VzIiwiY2FzZUluc2Vuc2l0aXZlUmF3IjoicGllY2VzIn0sImlzQ29tcHV0ZWQiOjB9LHsiaW5zSW5kZXgiOjczMywidG9rZW4iOnsibGluZU51bWJlciI6NDgsImNoYXJOdW1iZXIiOjMyLCJyYXciOiIxIiwiY2FzZUluc2Vuc2l0aXZlUmF3IjoiMSJ9LCJpc0NvbXB1dGVkIjoxfSx7Imluc0luZGV4Ijo3NDEsInRva2VuIjp7ImxpbmVOdW1iZXIiOjQ5LCJjaGFyTnVtYmVyIjowLCJyYXciOiJJRiIsImNhc2VJbnNlbnNpdGl2ZVJhdyI6ImlmIn0sImlzQ29tcHV0ZWQiOjB9LHsiaW5zSW5kZXgiOjc3OSwidG9rZW4iOnsibGluZU51bWJlciI6NDksImNoYXJOdW1iZXIiOjIxLCJyYXciOiJwaWVjZXMiLCJjYXNlSW5zZW5zaXRpdmVSYXciOiJwaWVjZXMifSwiaXNDb21wdXRlZCI6MH0seyJpbnNJbmRleCI6ODEzLCJ0b2tlbiI6eyJsaW5lTnVtYmVyIjo0OSwiY2hhck51bWJlciI6MzIsInJhdyI6IjIiLCJjYXNlSW5zZW5zaXRpdmVSYXciOiIyIn0sImlzQ29tcHV0ZWQiOjF9XSJpbnNUb0Z1bmN0aW9uIjp7IjE5MyI6eyJpbnNJbmRleCI6MTkzLCJ0b2tlbiI6eyJsaW5lTnVtYmVyIjoxNywiY2hhck51bWJlciI6OSwicmF3IjoiZHJhd0JvYXJkIiwiY2FzZUluc2Vuc2l0aXZlUmF3IjoiZHJhd2JvYXJkIn0sImlzQ29tcHV0ZWQiOjB9LCI0NDgiOnsiaW5zSW5kZXgiOjQ0OCwidG9rZW4iOnsibGluZU51bWJlciI6MjksImNoYXJOdW1iZXIiOjksInJhdyI6ImdldEluZGV4IiwiY2FzZUluc2Vuc2l0aXZlUmF3IjoiZ2V0aW5kZXgifSwiaXNDb21wdXRlZCI6MH0sIjQ3NSI6eyJpbnNJbmRleCI6NDc1LCJ0b2tlbiI6eyJsaW5lTnVtYmVyIjozMiwiY2hhck51bWJlciI6OSwicmF3IjoiZ2V0UGllY2VUZXh0IiwiY2FzZUluc2Vuc2l0aXZlUmF3IjoiZ2V0cGllY2V0ZXh0In0sImlzQ29tcHV0ZWQiOjB9LCI1OTkiOnsiaW5zSW5kZXgiOjU5OSwidG9rZW4iOnsibGluZU51bWJlciI6NDYsImNoYXJOdW1iZXIiOjksInJhdyI6ImluaXRCb2FyZCIsImNhc2VJbnNlbnNpdGl2ZVJhdyI6ImluaXRib2FyZCJ9LCJpc0NvbXB1dGVkIjowfX19";
    #endregion

    #region bytecode
    protected byte[] _byteCode = LaunchUtil.Unpack64(encodedByteCode);
    protected const string encodedByteCode = "SwMAAAEADQAAAA4BAA8AAAAOCQAHAAEAEAAAAA4JAAcBCAAIARkJAAEANgAAACMBAEMAAAAVCAAJACwCAQBJAAAAFQgBCQAsAisCKwIDCQAsAysDLAUBAAEAAAAsBgEAAQAAACsFAwEABAAAAAMtBAAAAAAACjAEKwIBAAEAAAABAAEAAAAOAQBXAgAAFgEABAAAACsGAQABAAAAESsFLgMBAAQAAAADKwQCCw0ACQAHBwEAwQAAABYBAP///38VKij/AQAAAAAACQAHCAEAAAAAACsCAQABAAAAAQD/////AwImCAgbAQC9AQAAJQgIGgEA/wAAACMBAL0BAAAVAQAAAAAACQAHCQEAAAAAACsCAQABAAAAAQD/////AwImCAkbAQCpAQAAJQgJGgEAOwEAACMBAKkBAAAVCAgJAAgJCQABAAsAAAAOCAgICQEAwAEAABYJAAcKAQAEAAAAKwYIChErBS4DAQAEAAAAAysEAgsNAAkABwsICwEA2wEAABYJCS8MCAwBAAEAAAABAAIAAAAOCAkBAAEAAAACCQAHCQEACQEAABUICAEAAQAAAAIJAAcIAQDNAAAAFSkXKigJAAkABw0JAAkABw7/CA4IDSsCAwIpFykXKigJAAkABw//CA8BAEkCAAABABYCAAABAAAAAAABACcCAAABAAEAAAABADgCAAABAAIAAAABAAMAAAAnAQD///9/CQkpFwEASQIAABUBAP///38JCSkXAQBJAgAAFQEA////fwkJKRcBAEkCAAAVFAEA////fwkJKRcpFyoo/wEAAAAAAAkABw4BAAAAAAArAwEAAQAAAAEA/////wMCJggOGwEASQMAACUIDhoBAJUCAAAjAQBJAwAAFQEAZAAAAAkAAQAdAAAADgEAWgAAABgJAAEAuwIAACMBAOUCAAAVAQABAAAACQAPAQAEAAAAKwYIDhErBS4DAQAEAAAAAysEAgwBAOUCAAAVAQBkAAAACQABAB0AAAAOAQBfAAAAGAkAAQALAwAAIwEANQMAABUBAAIAAAAJAA8BAAQAAAArBggOESsFLgMBAAQAAAADKwQCDAEANQMAABUIDgEAAQAAAAIJAAcOAQBjAgAAFSkXeyJ0eXBlcyI6e30iZnVuY3Rpb25zIjp7ImRyYXdib2FyZCI6eyJuYW1lIjoiZHJhd2JvYXJkIiwiaW5zSW5kZXgiOjE5MywidHlwZUNvZGUiOjAsInR5cGVJZCI6LTEsInBhcmFtZXRlcnMiOltdfSwiZ2V0aW5kZXgiOnsibmFtZSI6ImdldGluZGV4IiwiaW5zSW5kZXgiOjQ0OCwidHlwZUNvZGUiOjAsInR5cGVJZCI6MCwicGFyYW1ldGVycyI6W3sibmFtZSI6InkiLCJpbmRleCI6MSwidHlwZUNvZGUiOjAsInR5cGVJZCI6MH0seyJuYW1lIjoieCIsImluZGV4IjowLCJ0eXBlQ29kZSI6MCwidHlwZUlkIjowfV19LCJnZXRwaWVjZXRleHQiOnsibmFtZSI6ImdldHBpZWNldGV4dCIsImluc0luZGV4Ijo0NzUsInR5cGVDb2RlIjo5LCJ0eXBlSWQiOjAsInBhcmFtZXRlcnMiOlt7Im5hbWUiOiJ2YWx1ZSIsImluZGV4IjowLCJ0eXBlQ29kZSI6MCwidHlwZUlkIjowfV19LCJpbml0Ym9hcmQiOnsibmFtZSI6ImluaXRib2FyZCIsImluc0luZGV4Ijo1OTksInR5cGVDb2RlIjowLCJ0eXBlSWQiOi0xLCJwYXJhbWV0ZXJzIjpbXX19InN0cmluZ3MiOlt7InZhbHVlIjoiLiIsImluZGV4UmVmZXJlbmNlcyI6WzUzNF19LHsidmFsdWUiOiJBIiwiaW5kZXhSZWZlcmVuY2VzIjpbNTUxXX0seyJ2YWx1ZSI6IkIiLCJpbmRleFJlZmVyZW5jZXMiOls1NjhdfSx7InZhbHVlIjoiPyIsImluZGV4UmVmZXJlbmNlcyI6WzU4Nl19XX0=";
    #endregion
}
