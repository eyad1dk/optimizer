using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace ForgePC;

public sealed record HardwareControl(string Id,string Name,string Category,string Value,ValueOption[] Choices,string Note,bool Binary,string On,string Off,bool Supported)
{
 public string Effective(string value)=>value=="absent"?On:value;
 public string Format(string value)=>Choices.FirstOrDefault(c=>c.Value==value)?.Label??value;
}
public sealed record HardwareSnapshot(int Build,string OS,bool? Laptop,int Threads,long TotalMemory,long FreeMemory,JsonElement Cpu,JsonElement Graphics,JsonElement Network,JsonElement Disks,JsonElement Volumes,JsonElement Tcp,JsonElement Pagefiles,long UptimeSeconds,HardwareControl[] Controls,string[] Errors);

// Only this embedded program can run. User values travel through JSON stdin, never code.
public static class HardwareBackend
{
 private static readonly JsonSerializerOptions Json=new(){PropertyNameCaseInsensitive=true};
 private static readonly System.Collections.Concurrent.ConcurrentDictionary<string,HardwareControl> Known=new();
 public static bool Contains(string id)=>id.StartsWith("hw:",StringComparison.Ordinal);
 public static void ValidateId(string id)
 {
  if(id.StartsWith("hw:service:")&&ServicePreferences.Names.Contains(id[11..]))return;
  if(id is "hw:compression" or "hw:pagefile" or "hw:trim" or "hw:policy:dvr" or "hw:policy:ads" or "hw:policy:throttle" or "hw:policy:speech")return;
  var p=id.Split(':');if(p.Length is not(4 or 5)||p[0]!="hw"||p[1]!="net"||!Guid.TryParseExact(p[3],"D",out var guid)||guid.ToString("D")!=p[3])throw new InvalidDataException("Unknown hardware control.");
  if(p.Length==4&&p[2] is "dns" or "rss" or "rsc4" or "rsc6")return;
  if(p.Length==5&&p[2]=="prop") {var bytes=Convert.FromBase64String(p[4]);var key=new UTF8Encoding(false,true).GetString(bytes);if(Convert.ToBase64String(bytes)==p[4]&&key.Length is >0 and <=256&&!key.Any(char.IsControl))return;}
  throw new InvalidDataException("Invalid driver property identity.");
 }
 public static void Validate(string id,string value)
 {
  ValidateId(id);if(value.Length>16384||value.Contains('\0'))throw new InvalidDataException("Invalid hardware value.");
  if(id=="hw:pagefile") {using var d=JsonDocument.Parse(value);var root=d.RootElement;var automatic=root.GetProperty("Automatic").GetBoolean();var files=root.GetProperty("Files").EnumerateArray().Select(f=>new{Name=f.GetProperty("Name").GetString()!,InitialSize=f.GetProperty("InitialSize").GetUInt32(),MaximumSize=f.GetProperty("MaximumSize").GetUInt32()}).ToArray();if(files.Length>16||!automatic&&files.Length==0||files.Select(f=>f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=files.Length||files.Any(f=>!Regex.IsMatch(f.Name,@"^[A-Z]:\\pagefile\.sys$")||f.InitialSize>f.MaximumSize||f.MaximumSize>1048576))throw new InvalidDataException("Invalid pagefile snapshot.");var canonical=JsonSerializer.Serialize(new{Automatic=automatic,Files=files.OrderBy(f=>f.Name,StringComparer.OrdinalIgnoreCase).ToArray()});if(value!=canonical)throw new InvalidDataException("Pagefile state must use the canonical complete snapshot format.");return;}
  if(id.StartsWith("hw:policy:")){if(value is not("absent" or "0" or "1"))throw new InvalidDataException("Invalid policy value.");return;}
  if(id.StartsWith("hw:net:dns:")){if(value=="dhcp")return;ValidateDns(value);return;}
  if(id.StartsWith("hw:net:prop:")){if(value.Length is <1 or >1024||value.Any(char.IsControl))throw new InvalidDataException("Invalid property value.");return;}
  if(value is not("On" or "Off"))throw new InvalidDataException("Expected On or Off.");
 }
 public static string CustomDns(string text){var value="static:"+string.Join(',',text.Split([',',' ',';'],StringSplitOptions.RemoveEmptyEntries));ValidateDns(value);return value;}
 private static void ValidateDns(string value)
 {if(!value.StartsWith("static:"))throw new InvalidDataException("Invalid DNS mode.");var addresses=value[7..].Split(',');if(addresses.Length is <1 or >4||addresses.Distinct().Count()!=addresses.Length)throw new InvalidDataException("Enter 1–4 distinct IPv4 DNS servers.");foreach(var s in addresses){if(!System.Net.IPAddress.TryParse(s,out var ip)||ip.AddressFamily!=System.Net.Sockets.AddressFamily.InterNetwork||ip.ToString()!=s||ip.Equals(System.Net.IPAddress.Any)||ip.Equals(System.Net.IPAddress.Broadcast))throw new InvalidDataException("Enter canonical IPv4 DNS addresses.");}}
 public static OperationDefinition Definition(string id)
 {
  ValidateId(id);Known.TryGetValue(id,out var control);
  string category=id.StartsWith("hw:service:")?"Services":id.StartsWith("hw:net:")?"Network":id=="hw:trim"?"Storage":id is "hw:policy:ads" or "hw:policy:speech"?"Privacy":id=="hw:policy:throttle"?"CPU":id=="hw:policy:dvr"?"Gaming":"Memory";
  return new(id,1,control?.Name??id,category,"Change the detected Windows configuration using its documented system interface.",control?.Note??"Hardware or policy dependent. Review the saved original before restoring.",Documentation(id),Evidence.WorkloadDependent,"Advanced · system configuration",Restart:id=="hw:compression"||id=="hw:pagefile"||id.StartsWith("hw:policy:")||id.StartsWith("hw:net:prop:"),RequiresAdministrator:true);
 }
 public static string Method(string id)=>id switch {
  _ when id.StartsWith("hw:service:")=>"Get-Service; Start-Service or Stop-Service without Force; ServiceController.WaitForStatus; allowlisted optional services only",
  "hw:compression"=>"Get-MMAgent; Enable-MMAgent / Disable-MMAgent -MemoryCompression",
  "hw:pagefile"=>"Win32_ComputerSystem.AutomaticManagedPagefile and Win32_PageFileSetting via Get/Set/New/Remove-CimInstance; complete configuration snapshot",
  "hw:trim"=>"fsutil behavior query DisableDeleteNotify; fsutil behavior set DisableDeleteNotify NTFS 0|1",
  "hw:policy:dvr"=>@"HKLM\SOFTWARE\Policies\Microsoft\Windows\GameDVR, AllowGameDVR (DWORD), Windows 10 only",
  "hw:policy:throttle"=>@"HKLM\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling, PowerThrottlingOff (DWORD)",
  "hw:policy:speech"=>@"HKLM\SOFTWARE\Policies\Microsoft\InputPersonalization, AllowInputPersonalization (DWORD)",
  "hw:policy:ads"=>@"HKLM\SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo, DisabledByGroupPolicy (DWORD)",
  _ when id.StartsWith("hw:net:dns:")=>"Get-DnsClientServerAddress IPv4; Set-DnsClientServerAddress -InputObject (IPv4 instance) -ServerAddresses or -ResetServerAddresses",
  _ when id.StartsWith("hw:net:rss:")=>"Get-NetAdapterRss / Set-NetAdapterRss -Enabled",
  _ when id.StartsWith("hw:net:rsc")=>"Get-NetAdapterRsc / Set-NetAdapterRsc -IPv4Enabled or -IPv6Enabled",
  _=>"Get-NetAdapterAdvancedProperty / Set-NetAdapterAdvancedProperty -RegistryKeyword (driver-reported) -RegistryValue -NoRestart; exact adapter GUID"
 };
 public static string Documentation(string id)=>"https://learn.microsoft.com/"+(id.StartsWith("hw:service:")?"en-us/powershell/module/microsoft.powershell.management/stop-service":id.StartsWith("hw:net:")?"en-us/powershell/module/"+(id.Contains(":dns:")?"dnsclient/set-dnsclientserveraddress":"netadapter/set-netadapteradvancedproperty"):id=="hw:compression"?"en-us/powershell/module/mmagent/enable-mmagent":id=="hw:pagefile"?"en-us/windows/win32/cimwin32prov/win32-pagefilesetting":id=="hw:trim"?"en-us/windows-server/administration/windows-commands/fsutil-behavior":id.EndsWith(":throttle")?"en-us/windows/client-management/mdm/policy-csp-admx-power":id.EndsWith(":dvr")?"en-us/windows/client-management/mdm/policy-csp-applicationmanagement":"en-us/windows/client-management/mdm/policy-csp-privacy");
 public static string Format(string id,string value)=>Known.TryGetValue(id,out var c)?c.Format(value):value;
 public static HardwareSnapshot Inspect()
 {var s=Run<HardwareSnapshot>(new{Action="inspect"});foreach(var c in s.Controls){Validate(c.Id,c.Value);Known[c.Id]=c;}return s;}
 public static HardwareControl Read(string id)
 {ValidateId(id);var c=Run<HardwareControl>(new{Action="read",Id=id});if(c.Id!=id||!c.Supported)throw new IOException("Unexpected hardware response.");Validate(id,c.Value);Known[id]=c;return c;}
 public static void Write(string id,string expected,string target)
 {Validate(id,expected);Validate(id,target);if(!SystemCommands.IsAdministrator){string result="";var code=SystemCommands.ReadHelper(["--hardware-write",id,Encode(expected),Encode(target)],t=>result=t).GetAwaiter().GetResult();if(code!=0)throw new IOException(result);return;}WriteLocal(id,expected,target);}
 internal static void WriteLocal(string id,string expected,string target)
 {Validate(id,expected);Validate(id,target);var after=Run<HardwareControl>(new{Action="write",Id=id,Expected=expected,Target=target});if(after.Id!=id||after.Value!=target||Read(id).Value!=target)throw new IOException("Hardware read-back verification failed.");}
 internal static string Encode(string value)=>Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
 internal static string Decode(string value){if(value.Length>24000)throw new InvalidDataException("Oversized helper payload.");return new UTF8Encoding(false,true).GetString(Convert.FromBase64String(value));}
 internal static string Maintenance(string id,string argument)=>JsonSerializer.Serialize(Run<JsonElement>(new{Action="maintenance",Id=id,Argument=argument}),new JsonSerializerOptions{WriteIndented=true});
 private static T Run<T>(object request)
 {
  using var stream=typeof(HardwareBackend).Assembly.GetManifestResourceStream("ForgePC.HardwareBackend.ps1")??throw new IOException("Hardware backend is missing.");using var reader=new StreamReader(stream,Encoding.UTF8);var script="param($request)\n"+reader.ReadToEnd().Replace("$request = [Console]::In.ReadToEnd() | ConvertFrom-Json", "");var bootstrap="$global:ErrorActionPreference='Stop';[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false);[Console]::InputEncoding=[Text.UTF8Encoding]::new($false);$envelope=[Console]::In.ReadToEnd()|ConvertFrom-Json;& ([ScriptBlock]::Create($envelope.Program)) $envelope.Request";
  var start=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,@"WindowsPowerShell\v1.0\powershell.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardInputEncoding=new UTF8Encoding(false),StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};foreach(var a in new[]{"-NoLogo","-NoProfile","-NonInteractive","-EncodedCommand",Convert.ToBase64String(Encoding.Unicode.GetBytes(bootstrap))})start.ArgumentList.Add(a);
  using var process=Process.Start(start)??throw new IOException("Windows PowerShell could not start.");var output=ReadBounded(process.StandardOutput,2_000_000);var error=ReadBounded(process.StandardError,65536);process.StandardInput.Write(JsonSerializer.Serialize(new{Program=script,Request=request}));process.StandardInput.Close();
  if(!process.WaitForExit(120000)){process.Kill(true);throw new TimeoutException("Hardware operation timed out. Saved originals remain in Recovery; inspect state before retrying.");}Task.WaitAll(output,error);if(process.ExitCode!=0)throw new IOException(error.Result.Length==0?"Windows rejected the hardware operation.":error.Result);return JsonSerializer.Deserialize<T>(output.Result,Json)??throw new IOException("Invalid backend response.");
 }
 private static async Task<string> ReadBounded(StreamReader reader,int maximum){var b=new StringBuilder();var chunk=new char[4096];int n;bool tooLarge=false;while((n=await reader.ReadAsync(chunk))>0){if(b.Length+n<=maximum)b.Append(chunk,0,n);else tooLarge=true;}if(tooLarge)throw new IOException("Backend response exceeded its limit.");return b.ToString();}
}
