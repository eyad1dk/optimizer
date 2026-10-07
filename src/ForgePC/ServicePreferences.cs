using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace ForgePC;

public static class ServicePreferences
{
 public static string Classify(string name)=>new[]{"RpcSs","DcomLaunch","RpcEptMapper","WinDefend","mpssvc","BFE","EventLog","SamSs","LSM","Schedule","Winmgmt","Dhcp","Dnscache","nsi","PlugPlay","Power"}.Contains(name,StringComparer.OrdinalIgnoreCase)?"SYSTEM CRITICAL — read only":"CAUTION — feature dependencies must be reviewed";
 public static readonly string[] Names=["Spooler","bthserv","SysMain","WSearch","DiagTrack","XblAuthManager","XblGameSave","XboxGipSvc","XboxNetApiSvc"];
 public static bool Contains(string id)=>id.StartsWith("service:",StringComparison.Ordinal)&&Names.Contains(id[8..],StringComparer.Ordinal);
 public const string Documentation="https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-changeserviceconfigw";
 [StructLayout(LayoutKind.Sequential)] private struct Configuration{public uint Type,Start,Error;public IntPtr Binary,Group;public uint Tag;public IntPtr Dependencies,Account,Display;}
 [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr OpenSCManager(string? machine,string? database,uint access);
 [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr OpenService(IntPtr manager,string service,uint access);
 [DllImport("advapi32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(IntPtr handle);
 [DllImport("advapi32.dll",EntryPoint="QueryServiceConfigW",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceConfig(IntPtr service,IntPtr buffer,uint size,out uint needed);
 [DllImport("advapi32.dll",EntryPoint="QueryServiceConfig2W",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceConfig2(IntPtr service,uint level,out int value,uint size,out uint needed);
 [DllImport("advapi32.dll",EntryPoint="ChangeServiceConfigW",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool ChangeServiceConfig(IntPtr service,uint type,uint start,uint error,string? binary,string? group,IntPtr tag,string? dependencies,string? account,string? password,string? display);

 public static (uint Type,int Delayed) Parse(string value)
 {var parts=value.Split(':');if(parts.Length!=2||parts[0] is not("2" or "3" or "4")||parts[1] is not("0" or "1"))throw new InvalidDataException("Unsupported service startup configuration.");return(uint.Parse(parts[0]),int.Parse(parts[1]));}
 private static T WithService<T>(string id,uint access,Func<IntPtr,T> action)
 {
  if(!Contains(id))throw new InvalidDataException("Service is outside the optional-service allowlist.");
  var manager=OpenSCManager(null,null,1);if(manager==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
  try{var service=OpenService(manager,id[8..],access);if(service==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());try{return action(service);}finally{CloseServiceHandle(service);}}finally{CloseServiceHandle(manager);}
 }
 public static string Read(string id)=>WithService(id,1,handle=>{var buffer=Marshal.AllocHGlobal(8192);try{if(!QueryServiceConfig(handle,buffer,8192,out _))throw new Win32Exception(Marshal.GetLastWin32Error());var config=Marshal.PtrToStructure<Configuration>(buffer);if(!QueryServiceConfig2(handle,3,out var delayed,4,out _))throw new Win32Exception(Marshal.GetLastWin32Error());var value=$"{config.Start}:{(delayed==0?0:1)}";Parse(value);return value;}finally{Marshal.FreeHGlobal(buffer);}});
 public static void Write(string id,string expected,string target)
 {
  var(type,delayed)=Parse(target);var original=Parse(expected);if(delayed!=original.Delayed)throw new InvalidDataException("Delayed-start ownership must be preserved.");
  if(!SystemCommands.IsAdministrator)throw new UnauthorizedAccessException("Administrator access is required for service configuration.");
  WithService(id,3,handle=>{if(Read(id)!=expected)throw new InvalidOperationException("Service configuration changed since preview.");if(!ChangeServiceConfig(handle,uint.MaxValue,type,uint.MaxValue,null,null,IntPtr.Zero,null,null,null,null))throw new Win32Exception(Marshal.GetLastWin32Error());return true;});
 }
 public static string Format(string value){try{var(type,delayed)=Parse(value);return type switch{2=>delayed==1?"Automatic (delayed)":"Automatic",3=>"Manual",4=>"Disabled",_=>value};}catch(InvalidDataException){return value;}}
 public static IEnumerable<ValueOption> Choices(string current){try{var(_,delay)=Parse(current);return new[]{2,3,4}.Select(type=>new ValueOption($"{type}:{delay}",Format($"{type}:{delay}")));}catch(InvalidDataException){return [];}}
 public static string Purpose(string name)=>name switch
 {
  "Spooler"=>"Optional startup configuration for printing. Only useful when printing is not needed; no measured FPS benefit.",
  "bthserv"=>"Optional Bluetooth startup configuration. Can prevent discovery/pairing or device support; no measured performance gain.",
  "SysMain"=>"Manual troubleshooting of Windows prefetch behavior. Disabling can make app launches slower; retain the Windows default for ordinary use.",
  "WSearch"=>"Manual control of indexing startup. May reduce indexing activity but degrades indexed search; not a general performance recommendation.",
  "DiagTrack"=>"Control Connected User Experiences and Telemetry startup as an explicit privacy preference; not a security switch or FPS optimization.",
  "XblAuthManager"=>"Control Xbox authentication service startup. Disabling can prevent Xbox sign-in; no gaming boost established.",
  "XblGameSave"=>"Control Xbox save service startup. Disabling can interrupt save synchronization; no gaming boost established.",
  "XboxGipSvc"=>"Control Xbox accessory service startup. Disabling can impair accessory features; no gaming boost established.",
  "XboxNetApiSvc"=>"Control Xbox networking service startup. Disabling can impair multiplayer/network features; no ping reduction established.",
  _=>throw new InvalidDataException("Unknown service.")
 };
 public static string Disadvantages(string name)=>(name switch
 {
  "Spooler"=>"Printing, printer discovery and queued print workflows may fail until re-enabled.",
  "bthserv"=>"Bluetooth discovery, pairing or dependent device functionality may fail.",
  "SysMain"=>"App launch/load times may worsen; reduced prefetch activity is not an established net speed improvement.",
  "WSearch"=>"Search results can be slower or incomplete; mail/file indexing may stop.",
  "DiagTrack"=>"Connected diagnostics/telemetry collection can be unavailable; policy may restore the service. This does not disable all Windows telemetry.",
  "XblAuthManager"=>"Xbox authentication and sign-in-dependent game features may fail.",
  "XblGameSave"=>"Xbox save synchronization may stop or be delayed; confirm save state before changing.",
  "XboxGipSvc"=>"Xbox accessory integration or management features may fail.",
  "XboxNetApiSvc"=>"Xbox networking, party or multiplayer features may fail.",
  _=>throw new InvalidDataException("Unknown service.")
 })+" Only startup configuration changes; running state is untouched. Restore the captured type before using affected features.";
 public static readonly OperationDefinition[] Definitions=Names.Select(name=>new OperationDefinition("service:"+name,1,name+" startup configuration",name=="DiagTrack"?"Privacy":"Services",Purpose(name),Disadvantages(name),Documentation,Evidence.DocumentedBehavior,"Advanced · functionality", "Exact saved startup type and delayed-start flag; runtime state is not changed",false,true)).ToArray();
}
