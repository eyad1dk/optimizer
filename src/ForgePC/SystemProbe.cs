using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32;
namespace ForgePC;

public record SystemReading(DateTimeOffset At,double? Cpu,double? MemoryPercent,ulong? TotalMemory,ulong? FreeMemory,long? DiskTotal,long? DiskFree,bool? OnBattery,string Status,long DurationMs);
public record HardwareInventory(string OS,string Cpu,int LogicalProcessors,string Architecture,List<string> Graphics,List<string> Storage,string PowerContext);
public record ProcessEntry(int Id,long StartTicks,string Name,long Memory)
{ public string Display => $"{Name} · {Memory / 1048576d:0} MB"; public override string ToString()=>Display; }
public record InstalledApp(string Name,string Publisher,string Version,string Scope)
{ public string Display => $"{Name}  ·  {Version}  ·  {Publisher}  ·  {Scope}"; }
public interface ISystemProbe
{
 Task<SystemReading> SampleAsync(CancellationToken cancellation = default);
 Task<HardwareInventory> InventoryAsync(CancellationToken cancellation = default);
 Task<List<ProcessEntry>> ProcessesAsync(CancellationToken cancellation = default);
 Task<List<string>> StartupAsync(CancellationToken cancellation = default);
 Task<List<InstalledApp>> AppsAsync(CancellationToken cancellation = default);
 List<string> NetworkInventory();
}
public sealed class WindowsSystemProbe : ISystemProbe
{
 [StructLayout(LayoutKind.Sequential)] private struct Memory { public uint Length,Load; public ulong Total,Free,TotalPage,FreePage,TotalVirtual,FreeVirtual,Extended; }
 [StructLayout(LayoutKind.Sequential)] private struct Battery { public byte Ac,Flag,Percent,Reserved; public uint Life,FullLife; }
 [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref Memory memory);
 [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle,out long kernel,out long user);
 [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out Battery battery);
 private readonly Action<Exception> report;
 private readonly object sampleLock = new();
 private readonly Lazy<Task<HardwareInventory>> inventory;
 private long? previousTotal,previousIdle;
 private DateTimeOffset? previousAt;
 public WindowsSystemProbe(Action<Exception>? errors=null) {report=errors??(error=>Trace.TraceWarning("System probe: "+error.GetType().Name)); inventory = new(()=>Task.Run(ReadInventory)); }
 public static bool? BatteryState() => GetSystemPowerStatus(out var battery) && battery.Ac != 255 ? battery.Ac == 0 : null;
 public static string BatteryDescription()
 {
  if (!GetSystemPowerStatus(out var battery)) return "Power source unavailable";
  var source=battery.Ac switch { 0=>"Battery",1=>"AC",_=>"Unknown power source" };
  return source + (battery.Percent <= 100 ? $" · {battery.Percent}% battery" : " · battery percentage unavailable");
 }
 public Task<SystemReading> SampleAsync(CancellationToken cancellation = default) => Task.Run(() => {
  cancellation.ThrowIfCancellationRequested();
  lock(sampleLock)
  {
   var watch=Stopwatch.StartNew(); var at=DateTimeOffset.UtcNow; double? cpu=null; ulong? total=null,free=null;
   if(GetSystemTimes(out var idle,out var kernel,out var user))
   {
    var combined=kernel+user;
    if(previousTotal.HasValue && previousIdle.HasValue && previousAt.HasValue && (at-previousAt.Value).TotalMilliseconds>=250 && combined>previousTotal)
     cpu=Math.Clamp(100d * ((combined-previousTotal.Value)-(idle-previousIdle.Value))/(combined-previousTotal.Value),0,100);
    previousTotal=combined; previousIdle=idle; previousAt=at;
   }
   var memory=new Memory { Length=(uint)Marshal.SizeOf<Memory>() };
   if(GlobalMemoryStatusEx(ref memory)){ total=memory.Total; free=memory.Free; }
   long? diskTotal=null,diskFree=null;
   try { var drive=new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!); diskTotal=drive.TotalSize; diskFree=drive.AvailableFreeSpace; } catch(Exception error) { report(error); }
   return new SystemReading(at,cpu,total>0 ? 100d*(total.Value-free!.Value)/total.Value : null,total,free,diskTotal,diskFree,BatteryState(),
    total.HasValue && diskTotal.HasValue ? cpu.HasValue ? "Sampled" : "CPU warming up or unavailable" : "Some readings unavailable",watch.ElapsedMilliseconds);
  }
 },cancellation);
 public async Task<HardwareInventory> InventoryAsync(CancellationToken cancellation=default) => await inventory.Value.WaitAsync(TimeSpan.FromSeconds(15),cancellation);
 internal static List<Dictionary<string,object?>> Wmi(string scope,string query,params string[] properties)=>WmiBounded(scope,query,128,properties);
 internal static List<Dictionary<string,object?>> WmiBounded(string scope,string query,int limit,params string[] properties)
 {
  using var search=new ManagementObjectSearcher(new ManagementScope(scope),new ObjectQuery(query),
   new System.Management.EnumerationOptions { Timeout=TimeSpan.FromSeconds(5),ReturnImmediately=true,Rewindable=false });
  using var objects=search.Get(); var result=new List<Dictionary<string,object?>>();
  foreach(ManagementObject row in objects)
  { using(row) { var values=new Dictionary<string,object?>(); foreach(var property in properties) values[property]=row[property]; result.Add(values); } if(result.Count>=Math.Clamp(limit,1,2048))break; }
  return result;
 }
 private HardwareInventory ReadInventory()
 {
  string os=Environment.OSVersion.VersionString,cpu="Unknown"; var graphics=new List<string>(); var storage=new List<string>();
  using(var key=Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0")) cpu=key?.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? "Unknown";
  try { var row=Wmi(@"root\cimv2","SELECT Caption,Version,BuildNumber FROM Win32_OperatingSystem","Caption","Version","BuildNumber").FirstOrDefault(); if(row!=null)os=$"{row["Caption"]} · {row["Version"]} · build {row["BuildNumber"]}"; } catch(Exception error) { report(error); }
  try { graphics=Wmi(@"root\cimv2","SELECT Name,DriverVersion FROM Win32_VideoController","Name","DriverVersion").Select(r=>$"{r["Name"]} · driver {r["DriverVersion"]}").ToList(); } catch(Exception error) { Trace.TraceWarning("Graphics inventory: "+error.GetType().Name); graphics.Add("Graphics identity unavailable"); }
  try { storage=Wmi(@"root\Microsoft\Windows\Storage","SELECT FriendlyName,MediaType,BusType FROM MSFT_PhysicalDisk","FriendlyName","MediaType","BusType").Select(r=>$"{r["FriendlyName"]} · {(Convert.ToInt32(r["MediaType"]) switch { 3=>"HDD",4=>"SSD",_=>"Media type unknown" })} · bus type {r["BusType"]}").ToList(); } catch(Exception error) { Trace.TraceWarning("Storage inventory: "+error.GetType().Name); storage.Add("Storage media type unavailable; do not infer HDD/SSD from the drive letter"); }
  return new(os,cpu,Environment.ProcessorCount,RuntimeInformation.OSArchitecture.ToString(),graphics,storage,BatteryDescription());
 }
 public Task<List<ProcessEntry>> ProcessesAsync(CancellationToken cancellation=default)=>Task.Run(()=>{
  var result=new List<ProcessEntry>();
  foreach(var p in Process.GetProcesses()) { cancellation.ThrowIfCancellationRequested(); using(p) { try { result.Add(new(p.Id,p.StartTime.ToUniversalTime().Ticks,p.ProcessName,p.WorkingSet64)); } catch(Exception error) { report(error); } } }
  return result.OrderByDescending(p=>p.Memory).ToList();
 },cancellation);
 public Task<List<string>> StartupAsync(CancellationToken cancellation=default)=>Task.Run(()=>{
  var items=new HashSet<string>();
  foreach(var hive in new[]{RegistryHive.CurrentUser,RegistryHive.LocalMachine})
   foreach(var view in new[]{RegistryView.Registry64,RegistryView.Registry32})
   {
    cancellation.ThrowIfCancellationRequested();
    try { using var root=RegistryKey.OpenBaseKey(hive,view); using var key=root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
     if(key!=null) foreach(var name in key.GetValueNames()) items.Add($"{hive} · {view} · {name}"); } catch(Exception error) { report(error); items.Add($"{hive} · {view} · inspection restricted"); }
   }
  foreach(var folder in new[]{Environment.SpecialFolder.Startup,Environment.SpecialFolder.CommonStartup})
   try { var path=Environment.GetFolderPath(folder); if(Directory.Exists(path)) foreach(var file in Directory.GetFiles(path))items.Add($"{folder} · {Path.GetFileNameWithoutExtension(file)}"); } catch(Exception error) { report(error); items.Add($"{folder} · inspection restricted"); }
  return items.Order().ToList();
 },cancellation);
 public Task<List<InstalledApp>> AppsAsync(CancellationToken cancellation=default)=>Task.Run(()=>{
  var items=new Dictionary<string,InstalledApp>();
  foreach(var hive in new[]{RegistryHive.CurrentUser,RegistryHive.LocalMachine})
   foreach(var view in new[]{RegistryView.Registry64,RegistryView.Registry32})
   {
    cancellation.ThrowIfCancellationRequested();
    using var root=RegistryKey.OpenBaseKey(hive,view); using var uninstall=root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
    if(uninstall==null)continue;
    foreach(var id in uninstall.GetSubKeyNames())
    {
     cancellation.ThrowIfCancellationRequested();
     try { using var key=uninstall.OpenSubKey(id); var name=key?.GetValue("DisplayName") as string;
      if(string.IsNullOrWhiteSpace(name))continue;
      var publisher=key?.GetValue("Publisher") as string ?? "Unknown publisher"; var version=key?.GetValue("DisplayVersion") as string ?? "Unknown version";
      items.TryAdd(name+"|"+version+"|"+hive,new(name,publisher,version,hive.ToString())); } catch(Exception error) { report(error); }
    }
   }
  return items.Values.OrderBy(a=>a.Name).ToList();
 },cancellation);
 public List<string> NetworkInventory()
 {
  var items=new List<string>();
  foreach(var adapter in NetworkInterface.GetAllNetworkInterfaces())
   try { var properties=adapter.GetIPProperties(); items.Add($"{adapter.Name} · {adapter.NetworkInterfaceType} · {adapter.OperationalStatus}\nDNS: {string.Join(", ",properties.DnsAddresses.Select(a=>a.ToString()))}\nIdentifiers and addresses stay local; not included in default exports."); } catch(Exception error) { report(error); items.Add("Adapter details restricted"); }
  return items;
 }
}
public sealed class WindowsCapabilityService(WindowsSettings settings) : ICapabilityService
{
 [DllImport("powrprof.dll")] private static extern uint PowerSettingAccessCheck(uint accessor,ref Guid setting);
 public Capability Check(string id,string target)=>CheckCore(id,target,false);
 public Capability CheckRestore(string id,string target)=>CheckCore(id,target,true);
 private Capability CheckCore(string id,string target,bool restoring)
 {
  try
  {
   Catalog.ValidateTarget(id,target);
   if(id=="hw:pagefile"&&!restoring)return new(false,"Pagefile writes are unsupported until multi-step failure recovery is validated. Use Windows Virtual Memory settings; existing saved recovery remains available.");
   if(Environment.OSVersion.Version.Build<19045)return new(false,"This preview requires Windows 10 22H2 or Windows 11. Servicing eligibility must be checked separately.");
   if(id=="power")
   {
    if(!settings.Plans().Any(p=>p.Id==target))return new(false,"Desired plan is not installed; OEM/Modern Standby PCs may expose fewer plans.");
    var guid=Guid.Parse(target); var access=PowerSettingAccessCheck(16,ref guid);
    if(access!=0||PowerSettingAccessCheck(19,ref guid)!=0)return new(false,"Windows policy or permissions restrict this power plan.");
    if(!restoring&&target=="8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c" && WindowsSystemProbe.BatteryState()!=false)
     return new(false,"High performance is not offered while on battery or when AC state is unknown.");
   }
   if(ProcessorPower.Ids.Contains(id)){var index=ProcessorPower.SettingId(id);var scheme=ProcessorPower.Parse(target).Scheme;if(scheme!=ProcessorPower.ActiveScheme())return new(false,"The captured processor scheme is no longer active.");if(PowerSettingAccessCheck(id.EndsWith("-ac")?0u:1u,ref index)!=0||PowerSettingAccessCheck(19,ref scheme)!=0)return new(false,"Windows policy restricts this processor setting.");}
   settings.Read(id);
   return new(true,"Available · Windows validates policy again at write and read-back");
  }
  catch { return new(false,"Current value, capability or policy could not be determined. No automatic change is available."); }
 }
}
public record EndpointMeasurement(string Status,int Sent,int Received,double? MedianMs,string Explanation);
public sealed class NetworkDiagnostics
{
 public static string ValidateEndpoint(string endpoint)
 {
  endpoint=endpoint.Trim();
  if(endpoint.Length is <1 or >253 || Uri.CheckHostName(endpoint)==UriHostNameType.Unknown || endpoint.Contains('/') || endpoint.Contains('@'))
   throw new InvalidDataException("Enter a hostname or IP address, without a URL, path or credentials.");
  return endpoint;
 }
 public async Task<EndpointMeasurement> MeasureAsync(string endpoint,CancellationToken token=default)
 {
  endpoint=ValidateEndpoint(endpoint); var times=new List<long>();
  for(var i=0;i<5;i++)
  {
   token.ThrowIfCancellationRequested();
   try { using var ping=new Ping(); var reply=await ping.SendPingAsync(endpoint,TimeSpan.FromSeconds(2),new byte[32],null,token).WaitAsync(TimeSpan.FromSeconds(3),token); if(reply.Status==IPStatus.Success)times.Add(reply.RoundtripTime); }
   catch(PingException) { } catch(TimeoutException) { }
   if(i<4)await Task.Delay(250,token);
  }
  times.Sort();
  return new(times.Count==0?"No ICMP replies":"Completed",5,times.Count,times.Count>0?(times.Count%2==1?times[times.Count/2]:(times[times.Count/2-1]+times[times.Count/2])/2d):null,
   "Five ICMP attempts, 2-second timeout each. ICMP may be blocked or deprioritized. This endpoint sample does not measure game-server ping, DNS speed, ISP improvements or application latency.");
 }
}
