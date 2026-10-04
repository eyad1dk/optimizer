using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
namespace ForgePC;

public static class Maintenance
{
 public static void Validate(string id,string argument)
 {if(id=="dns-test"){NetworkDiagnostics.ValidateEndpoint(argument);return;}if(id is "renew" or "release"){if(!Guid.TryParseExact(argument,"D",out _))throw new InvalidDataException("No adapter was selected.");return;}if(id is "startup-info" or "delivery-scan" or "delivery-clean"){if(argument.Length!=0)throw new InvalidDataException("Unexpected maintenance argument.");return;}throw new InvalidDataException("Unknown maintenance action.");}
 public static async Task<string> Run(string id,string argument)
 {Validate(id,argument);if(id is "renew" or "release" or "delivery-clean"&&!SystemCommands.IsAdministrator){string output="";var code=await SystemCommands.ReadHelper(["--maintenance",id,HardwareBackend.Encode(argument)],t=>output=t);if(code!=0)throw new IOException(output);return output;}return await Task.Run(()=>Local(id,argument));}
 internal static string Local(string id,string argument){Validate(id,argument);return HardwareBackend.Maintenance(id,argument);}
 [StructLayout(LayoutKind.Sequential,Pack=8)]private struct BinInfo{public uint Size;public long Bytes,Items;}
 [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern int SHQueryRecycleBinW(string? root,ref BinInfo info);
 [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern int SHEmptyRecycleBinW(IntPtr owner,string? root,uint flags);
 public static (long Bytes,long Items) ReadBin(){var info=new BinInfo{Size=(uint)Marshal.SizeOf<BinInfo>()};Marshal.ThrowExceptionForHR(SHQueryRecycleBinW(null,ref info));return(info.Bytes,info.Items);}
 public static string EmptyBin(){var before=ReadBin();Marshal.ThrowExceptionForHR(SHEmptyRecycleBinW(IntPtr.Zero,null,7));var after=ReadBin();return $"Recycle Bin read-back: {after.Items} items / {after.Bytes} bytes remain; before: {before.Items} items / {before.Bytes} bytes. Permanent deletion; no app rollback.";}
}
