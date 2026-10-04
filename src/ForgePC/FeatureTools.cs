using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Windows;

namespace ForgePC;

public record ServiceEntry(string Name,string Display,string State,string StartMode,string Description) {public string Summary=>$"{Display} ({Name}) · {State} · {StartMode}";}
public record FeatureLink(string Title,string Destination,string Detail);
public sealed class FeatureTools : Observable
{
 public Func<Task<string>>? RefreshHardwareInfo{get;set;}
 public Func<bool> ExternalBusy{get;set;}=()=>false;
 public Task RestoreAllStartupAsync()=>Task.Run(()=>{var failures=0;foreach(var item in startup.History().Where(r=>r.State!="restored")){try{startup.Restore(item.Id);}catch(Exception error){logger.Error(error);failures++;}}if(failures>0)throw new InvalidOperationException(failures+" startup originals still need recovery review.");});
 private readonly StartupManager startup;
 private List<ServiceEntry> allServices=[];public ObservableCollection<ServiceEntry> Services{get;}=[];
 public string[] ServiceFilters{get;}=["All","Running","Stopped","Automatic","Manual","Disabled"];
 private string serviceFilter="All",serviceSearch="";
 public string ServiceFilter{get=>serviceFilter;set{if(ServiceFilters.Contains(value)){Set(ref serviceFilter,value);FilterServices();}}}
 public string ServiceSearch{get=>serviceSearch;set{Set(ref serviceSearch,value);FilterServices();}}
 private void FilterServices(){Services.Clear();foreach(var item in allServices.Where(s=>(ServiceFilter=="All"||ServiceFilter is "Running" or "Stopped"?ServiceFilter=="All"||s.State==ServiceFilter:s.StartMode==(ServiceFilter=="Automatic"?"Auto":ServiceFilter))&&(s.Name+s.Display+s.Description).Contains(ServiceSearch,StringComparison.OrdinalIgnoreCase)))Services.Add(item);}
 public void RefreshAvailability(){foreach(var command in new[]{Refresh,Run,RefreshStartup,DisableStartup,RestoreStartup})command.Refresh();}
 public ObservableCollection<StartupRegistration> StartupEntries{get;}=[];
 public Command RefreshStartup{get;}public Command DisableStartup{get;}public Command RestoreStartup{get;}
 private string startupSummary="Current-user Run registrations; Windows startup approval can override them.";
 public string StartupSummary{get=>startupSummary;private set=>Set(ref startupSummary,value);}
 private readonly IUserInteraction ui;private readonly LocalLogger logger;private string section="CPU",information="Refresh to read current configuration.",commandOutput="No system command has run.";private bool busy;
 public string Section{get=>section;set{Set(ref section,value);Information="Refresh to read current "+value+" information.";Links.Clear();foreach(var link in LinksFor(value))Links.Add(link);Tasks.Clear();foreach(var task in SystemCommands.Tasks.Where(t=>value=="Diagnostics"&&t.Id!="dns-flush"||value=="Storage"&&t.Id is "trim-check" or "drive-analyze" or "drive-optimize" or "disk-scan" or "component-analyze" or "component-clean"||value=="Cleanup"&&t.Id is "component-analyze" or "component-clean"))Tasks.Add(task);}}
 public string Information{get=>information;private set=>Set(ref information,value);}
 public string CommandOutput{get=>commandOutput;private set=>Set(ref commandOutput,value);}
 public string Privilege=>SystemCommands.IsAdministrator?"Administrator session":"Standard user · individual system tasks request elevation";
 public bool Busy{get=>busy;private set{Set(ref busy,value);Refresh.Refresh();Run.Refresh();RefreshStartup.Refresh();DisableStartup.Refresh();RestoreStartup.Refresh();}}
 public ObservableCollection<FeatureLink> Links{get;}=[];
 public ObservableCollection<SystemTask> Tasks{get;}=[];
 public Command Refresh{get;} public Command Run{get;} public Command Open{get;} public Command OpenLogs{get;}
 public FeatureTools(IUserInteraction interaction,LocalLogger log,string dataRoot)
 {
  ui=interaction;logger=log;startup=new(Path.Combine(dataRoot,"startup-recovery"));
  RefreshStartup=new(async _=>{Busy=true;try{var entries=await Task.Run(startup.Read);StartupEntries.Clear();foreach(var entry in entries)StartupEntries.Add(entry);StartupSummary=$"{entries.Count} current-user Run and saved registrations. RunOnce, machine, scheduled and packaged entries remain read-only through Windows inventory.";}finally{Busy=false;}},Error,()=>!Busy&&!ExternalBusy());
  DisableStartup=new(async p=>{if(p is not StartupRegistration entry||!ui.Confirm("Disable this current-user Run registration? Its exact unexpanded command and registry type are saved first. Other startup sources may still launch the app.\n\n"+entry.Name,"Review startup registration"))return;Busy=true;try{await Task.Run(()=>startup.Disable(entry));logger.Write("startup","registration","disabled and verified");await RefreshEntries();StartupSummary="Registration disabled and verified. The saved entry remains available for Restore.";}finally{Busy=false;}},Error,()=>!Busy&&!ExternalBusy());
  RestoreStartup=new(async p=>{if(p is not StartupRegistration entry||entry.RecoveryId==null||!ui.Confirm("Restore this exact saved Run registration? It may launch the app at your next sign-in. Existing external values will be preserved.\n\n"+entry.Name+"\n"+entry.Value,"Restore startup registration"))return;Busy=true;try{await Task.Run(()=>startup.Restore(entry.RecoveryId));logger.Write("startup","registration","restored and verified");await RefreshEntries();StartupSummary="Original registration restored and verified.";}finally{Busy=false;}},Error,()=>!Busy&&!ExternalBusy());

  Refresh=new(async _=>{var requested=Section;Busy=true;Information="Reading local configuration…";try{if(RefreshHardwareInfo!=null&&requested is "CPU" or "GPU" or "Memory" or "Network" or "Storage"){Information=await RefreshHardwareInfo();return;}if(requested=="Services"){var records=await Task.Run(()=>WindowsSystemProbe.WmiBounded(@"root\cimv2","SELECT Name,DisplayName,State,StartMode,Description FROM Win32_Service",2048,"Name","DisplayName","State","StartMode","Description")).WaitAsync(TimeSpan.FromSeconds(25));allServices=records.Select(r=>new ServiceEntry(r["Name"]?.ToString()??"Unknown",r["DisplayName"]?.ToString()??"Unknown",r["State"]?.ToString()??"Unknown",r["StartMode"]?.ToString()??"Unknown",r["Description"]?.ToString()??"No description")).OrderBy(s=>s.Display).ToList();FilterServices();Information=$"{allServices.Count} services read (bounded at 2,048). Publisher classification is unavailable; no Microsoft/third-party label is guessed. Review allowlisted startup controls in Optimize. Running services are not stopped automatically.";return;}var result=await Task.Run(()=>Inspect(requested)).WaitAsync(TimeSpan.FromSeconds(25));if(Section==requested)Information=result;}finally{Busy=false;}},Error,()=>!Busy&&!ExternalBusy());
  Run=new(async p=>{if(p is not SystemTask task)return;if(!ui.Confirm(task.Warning+"\n\n"+(task.Administrator?"Administrator permission will be requested if needed. ":"")+"The fixed Windows command runs in the app. Wait for it to finish; closing is blocked during execution.",task.Name))return;Busy=true;CommandOutput="Starting "+task.Name+"…\n";logger.Write("system-task",task.Id,"started");try{var code=await SystemCommands.RunAsync(task.Id,text=>Application.Current.Dispatcher.Invoke(()=>{CommandOutput+=text;if(CommandOutput.Length>65536)CommandOutput="[Earlier output omitted]\n"+CommandOutput[^60000..];}));if(code==0&&task.Id is "sfc-repair" or "dism-repair" or "component-clean" or "drive-optimize"){var verification=task.Id switch{"sfc-repair"=>"sfc-verify","component-clean"=>"component-analyze","drive-optimize"=>"drive-analyze",_=>"dism-scan"};CommandOutput+="\nIndependent follow-up: "+verification+"\n";code=await SystemCommands.RunAsync(verification,text=>Application.Current.Dispatcher.Invoke(()=>{CommandOutput+=text;if(CommandOutput.Length>65536)CommandOutput="[Earlier output omitted]\n"+CommandOutput[^60000..];}));}logger.Write("system-task",task.Id,"exit:"+code);Information=code==0?"Command completed. Review findings in Command details.":"× Command failed (exit "+code+"). Review Command details.";}finally{Busy=false;}},Error,()=>!Busy&&!ExternalBusy());
  Open=new(p=>{if(p is FeatureLink link&&AllLinks.Any(x=>x==link)){var destination=link.Destination;Process.Start(new ProcessStartInfo(destination.StartsWith("ms-settings:")?destination:Path.Combine(Environment.SystemDirectory,destination)){UseShellExecute=true});}else throw new InvalidOperationException("Unknown Windows control.");return Task.CompletedTask;},Error);
  OpenLogs=new(_=>{var folder=Path.Combine(dataRoot,"logs");Directory.CreateDirectory(folder);Process.Start(new ProcessStartInfo(folder){UseShellExecute=true});return Task.CompletedTask;},Error);
 }
 private async Task RefreshEntries(){var entries=await Task.Run(startup.Read);StartupEntries.Clear();foreach(var entry in entries)StartupEntries.Add(entry);}
 private void Error(Exception error){var reference=logger.Error(error);CommandOutput+="\nFailed: "+error.Message;Information="This action did not complete. "+(error is System.ComponentModel.Win32Exception native&&native.NativeErrorCode==1223?"Administrator permission was cancelled.":error is TimeoutException?"The read timed out. Try again after Windows finishes other work.":error is InvalidOperationException?error.Message:"Windows denied or could not complete the request.")+" Reference "+reference;}
 private static string Rows(string query,params string[] properties)
 {
  var rows=WindowsSystemProbe.Wmi(@"root\cimv2",query,properties);return rows.Count==0?"No available records.":string.Join("\n\n",rows.Select(r=>string.Join("\n",properties.Select(p=>p+": "+(r.GetValueOrDefault(p)??"Unavailable")))));
 }
 internal static string Inspect(string page)=>page switch
 {
  "Power"=>"Review installed power plans in Optimize. Windows sleep settings remain a separate Windows control.",
  "CPU"=>Rows("SELECT Name,NumberOfCores,NumberOfLogicalProcessors,CurrentClockSpeed,MaxClockSpeed,Architecture FROM Win32_Processor","Name","NumberOfCores","NumberOfLogicalProcessors","CurrentClockSpeed","MaxClockSpeed","Architecture")+"\n\nActive power scheme: "+ProcessorPower.ActiveScheme()+"\nClock values are firmware-reported MHz, not continuous effective-clock measurements. Scheduler internals are not inferred from registry defaults.",
  "GPU"=>System.Text.Json.JsonSerializer.Serialize(GraphicsInventory.Read(),new System.Text.Json.JsonSerializerOptions{WriteIndented=true})+"\n\n"+Rows("SELECT Name,DriverVersion,AdapterRAM,VideoProcessor,CurrentRefreshRate,CurrentHorizontalResolution,CurrentVerticalResolution FROM Win32_VideoController","Name","DriverVersion","VideoProcessor","CurrentRefreshRate","CurrentHorizontalResolution","CurrentVerticalResolution")+"\n\nDXGI reports dedicated and shared memory in bytes above; shared memory is not dedicated VRAM. Live GPU activity is sampled separately on Overview. Legacy AdapterRAM can truncate modern VRAM sizes, so it is not shown. HAGS/VRR availability must be checked in Windows graphics settings.",
  "Memory"=>Rows("SELECT TotalVisibleMemorySize,FreePhysicalMemory,TotalVirtualMemorySize,FreeVirtualMemory FROM Win32_OperatingSystem","TotalVisibleMemorySize","FreePhysicalMemory","TotalVirtualMemorySize","FreeVirtualMemory")+"\nMemory values above are KiB.\n\n"+Rows("SELECT Name,AllocatedBaseSize,CurrentUsage,PeakUsage FROM Win32_PageFileUsage","Name","AllocatedBaseSize","CurrentUsage","PeakUsage")+"\nPagefile values are MiB. Standby-list size is not available from this probe. Use Windows-managed sizing unless your workload requires otherwise.",
  "Storage"=>string.Join("\n",DriveInfo.GetDrives().Select(d=>{try{return d.IsReady?$"{d.Name} · {d.DriveType} · {d.TotalSize/1073741824d:0.0} GB total · {d.AvailableFreeSpace/1073741824d:0.0} GB free":"Drive not ready";}catch(IOException){return "Drive inaccessible";}}))+"\n\n"+Rows("SELECT Model,InterfaceType,Size,Status FROM Win32_DiskDrive","Model","InterfaceType","Size","Status")+"\nStatus is provider-reported, not a comprehensive SMART health test.",
  "Services"=>Rows("SELECT Name,DisplayName,State,StartMode,Description FROM Win32_Service","Name","DisplayName","State","StartMode","Description")+"\n\nRead-only bounded inventory (up to 128 records). Publisher classification is unavailable. Use Windows Services for manual management; no service is automatically disabled.",
  "Startup"=>Rows("SELECT Name,Location,User,Command FROM Win32_StartupCommand","Name","Location","User","Command")+"\n\nPartial Windows startup inventory. Task Scheduler and packaged startup apps require separate inspection. No impact rating is fabricated.",
  "Network"=>string.Join("\n\n",NetworkInterface.GetAllNetworkInterfaces().Select(n=>{try{var p=n.GetIPProperties();return $"{n.Name} · {n.OperationalStatus} · {n.NetworkInterfaceType}\nLink: {n.Speed/1000000d:0.0} Mbps\nAddresses: {string.Join(", ",p.UnicastAddresses.Select(a=>a.Address))}\nDNS: {string.Join(", ",p.DnsAddresses)}";}catch(NetworkInformationException){return "Adapter details unavailable";}}))+"\nLink speed is not internet throughput. DNS and adapter addresses stay local.",
  "Diagnostics"=>Rows("SELECT Caption,Version,BuildNumber,OSArchitecture,LastBootUpTime FROM Win32_OperatingSystem","Caption","Version","BuildNumber","OSArchitecture","LastBootUpTime")+"\n\n"+Rows("SELECT Manufacturer,Product FROM Win32_BaseBoard","Manufacturer","Product")+"\n\n"+Rows("SELECT Manufacturer,SMBIOSBIOSVersion,ReleaseDate FROM Win32_BIOS","Manufacturer","SMBIOSBIOSVersion","ReleaseDate"),
  "Gaming"=>"Choose a reviewed game session in Profiles. Display/driver readiness and frame-time imports are in Diagnostics. GPU driver-specific controls are informational; Windows owns the supported per-app graphics interface.",
  "Windows"=>"Appearance changes are opt-in. Open Optimize for reversible native effects, menu delay and input preferences. Windows Settings provides the remaining version-dependent controls.",
  "Privacy"=>"Review each permission in Windows. Managed policies and edition limits remain authoritative. Defender, Firewall, security updates and core security services are not modified.",
  "Cleanup"=>"Scan and choose Windows-managed cleanup categories. Review Windows estimates before deleting. Deletion cannot promise exact undo; personal folders are never selected by EZoptimizer. Project-folder inspection is read-only in Diagnostics.",
  _=>"Select a section to inspect."
 };
 private static readonly (string Page,FeatureLink Link)[] Entries=[
  ("Power",new("Advanced power settings","powercfg.cpl","Windows control · configure power plans")),
  ("GPU",new("Per-app GPU preference / HAGS / VRR","ms-settings:display-advancedgraphics","Windows control · availability depends on GPU, driver and Windows build")),
  ("GPU",new("Display refresh rate","ms-settings:display-advanced","Windows control")),
  ("Memory",new("Pagefile configuration","SystemPropertiesAdvanced.exe","Windows control · Performance > Advanced > Virtual memory")),
  ("Memory",new("Windows Memory Diagnostic","MdSched.exe","Windows control · offers a restart; save work first")),
  ("Gaming",new("Game Mode","ms-settings:gaming-gamemode","Windows control")),
  ("Gaming",new("Background recording / Game DVR","ms-settings:gaming-gamedvr","Windows control · recording is optional")),
  ("Gaming",new("Game Bar","ms-settings:gaming-gamebar","Windows control")),
  ("Gaming",new("Notifications","ms-settings:notifications","Windows control")),
  ("Network",new("Adapter and DNS settings","ms-settings:network-status","Windows control · changing DNS can affect VPN or managed networks")),
  ("Storage",new("Optimize drives","dfrgui.exe","Windows control · Windows chooses the operation for the drive")),
  ("Startup",new("Startup apps","ms-settings:startupapps","Windows control · enable or disable apps")),
  ("Startup",new("Task Scheduler","taskschd.msc","Windows control · review scheduled startup tasks")),
  ("Services",new("Windows Services","services.msc","Windows control · changes here are outside EZoptimizer recovery")),
  ("Windows",new("Visual effects","SystemPropertiesPerformance.exe","Windows control")),
  ("Windows",new("Taskbar settings","ms-settings:taskbar","Windows control · build-dependent options")),
  ("Privacy",new("Advertising and personalization","ms-settings:privacy-general","Windows control")),
  ("Privacy",new("Diagnostic data and feedback","ms-settings:privacy-feedback","Windows control · edition and policy limits apply")),
  ("Privacy",new("Location permissions","ms-settings:privacy-location","Windows control")),
  ("Privacy",new("Typing personalization","ms-settings:privacy-speechtyping","Windows control")),
  ("Cleanup",new("Temporary file categories and estimates","ms-settings:storagesense","Windows control · review selected categories before deleting")),
  ("Cleanup",new("Windows Disk Cleanup","cleanmgr.exe","Windows control · select categories and confirm deletion")),
  ("Diagnostics",new("Windows Update status","ms-settings:windowsupdate","Windows control"))
 ];
 public static IEnumerable<Guidance> SearchGuides=>Entries.Select(e=>new Guidance(e.Link.Title,e.Page,e.Link.Detail,"Open Windows control",e.Link.Destination,"Supported Windows interface"));
 public static IEnumerable<FeatureLink> AllLinks=>Entries.Select(e=>e.Link);
 private static IEnumerable<FeatureLink> LinksFor(string page)=>Entries.Where(e=>e.Page==page).Select(e=>e.Link);
}