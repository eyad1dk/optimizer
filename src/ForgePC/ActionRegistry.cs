using System.IO;
namespace ForgePC;

public record ActionDefinition(string Id,string Category,string Method,bool Administrator,string Verification,string Recovery,string? Executable=null)
{
 public Capability Supported()=>Id=="delivery-clean"?new(false,"Complete cache accounting is unavailable; use Windows Storage cleanup."):Environment.OSVersion.Version.Build<19045?new(false,"Windows 10 22H2 or Windows 11 required."):Executable!=null&&!File.Exists(Path.Combine(Environment.SystemDirectory,Executable))?new(false,"Windows command is unavailable."):new(true,"Platform available; device, cmdlet and permissions are checked on execution.");
}
public static partial class Catalog
{
 public static readonly SystemTask[] SystemTasks=[
  new("component-analyze","Analyze Windows component store","dism.exe",["/Online","/Cleanup-Image","/AnalyzeComponentStore"],true,"Reports Windows component-store cleanup estimates. It is not the Windows Update download cache."),
  new("component-clean","Clean superseded Windows components","dism.exe",["/Online","/Cleanup-Image","/StartComponentCleanup"],true,"Windows servicing removes superseded components. Permanent cleanup; no app rollback. Does not use ResetBase or disable Windows Update."),
  new("sfc-repair","Repair protected system files","sfc.exe",["/scannow"],true,"Repairs Windows protected files. Permanent servicing operation; no exact app rollback. Save work first. Follow-up verification runs automatically."),
  new("dism-repair","Repair Windows component store","dism.exe",["/Online","/Cleanup-Image","/RestoreHealth"],true,"Repairs the component store and may download repair content. Permanent servicing operation; no exact app rollback. Follow-up ScanHealth runs automatically."),
  new("drive-analyze","Analyze system drive","defrag.exe",[Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\'),"/A","/V"],true,"Analyzes the system volume using Windows Optimize Drives; does not run defragmentation."),
  new("drive-optimize","Optimize system drive for its media type","defrag.exe",[Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\'),"/O","/U","/V"],true,"Windows selects optimization appropriate for the drive type (/O). Can cause disk activity; cannot be reversed. No forced HDD defrag on SSD."),
  new("disk-scan","Scan system volume","chkdsk.exe",[Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\'),"/scan"],true,"Online NTFS scan. Windows may schedule required repairs. Does not force dismount; unsupported filesystems return an error."),
  new("dns-flush","Flush DNS cache","ipconfig.exe",["/flushdns"],true,"Clears the Windows resolver cache. New lookups rebuild it; this does not lower network latency."),
  new("dism-check","DISM quick health check","dism.exe",["/Online","/Cleanup-Image","/CheckHealth"],true,"Reads the component-store health flag. It does not perform a full scan or repair."),
  new("dism-scan","DISM health scan","dism.exe",["/Online","/Cleanup-Image","/ScanHealth"],true,"A thorough component-store scan can take several minutes."),
  new("sfc-verify","Verify protected system files","sfc.exe",["/verifyonly"],true,"Scans protected system files without repairing them. This can take several minutes."),
  new("trim-check","Check TRIM notification configuration","fsutil.exe",["behavior","query","DisableDeleteNotify"],true,"Reads deletion-notification configuration. A configured flag alone does not prove a particular drive supports TRIM.")
 ];

 public static IReadOnlyList<ActionDefinition> Actions=>SystemTasks.Select(t=>new ActionDefinition(t.Id,t.Id.StartsWith("dns")?"Network":t.Id.StartsWith("drive")||t.Id.StartsWith("trim")||t.Id.StartsWith("disk")?"Storage":"Diagnostics",t.Executable+" "+string.Join(" ",t.Arguments),t.Administrator,"Exit status plus displayed native findings; repair/cleanup actions run an independent follow-up when available.","Maintenance has no exact app rollback.",t.Executable)).Concat(CacheCatalog.All.Select(c=>new ActionDefinition("cache:"+c.Id,"Cleanup","Bounded scan, verified native file handles, delete disposition, rescan",c.Administrator,"Count actual verified deletions and logical bytes; report locked/skipped/remaining files.","Permanent deletion; no restore."))).Concat(new[]{
  new ActionDefinition("dns-test","Network","Resolve-DnsName",false,"Return actual answers, elapsed time and errors.","Read only"),
  new ActionDefinition("gateway","Network","Active adapter gateway + Ping.SendPingAsync",false,"Report each response or timeout; no assumed Internet connectivity.","Read only"),
  new ActionDefinition("renew","Network","Win32_NetworkAdapterConfiguration.RenewDHCPLease",true,"Return code and current lease/address readback.","DHCP server chooses address; exact lease rollback unavailable."),
  new ActionDefinition("release","Network","Win32_NetworkAdapterConfiguration.ReleaseDHCPLease",true,"Return code and adapter IPv4 readback.","Renew can request a lease; exact original lease cannot be restored."),
  new ActionDefinition("restore-network","Network","Journal reverse-order conditional restore",true,"Read exact original values after each write.","Restores EZoptimizer changes only; not Windows Network Reset."),
  new ActionDefinition("startup-info","Startup","Registry, Startup folders, scheduled tasks, packaged startup declarations",false,"Display actual inventory; unsupported sources reported.","Read only"),
  new ActionDefinition("delivery-scan","Cleanup","Get-DeliveryOptimizationStatus",false,"Active-job inventory only; complete cache byte count unavailable.","Read only"),
  new ActionDefinition("delivery-clean","Cleanup","Delete-DeliveryOptimizationCache",true,"Unsupported in this preview: complete before/after cache accounting unavailable.","Permanent deletion; no restore."),
  new ActionDefinition("bin-scan","Cleanup","SHQueryRecycleBinW",false,"Native item and byte counts.","Read only"),
  new ActionDefinition("bin-clean","Cleanup","SHQueryRecycleBinW / SHEmptyRecycleBinW / SHQueryRecycleBinW",false,"Before and after native counts; concurrent activity may change totals.","Permanent deletion; no restore."),
  new ActionDefinition("restart-explorer","Windows","Verified current-session shell process restart",false,"Detect a new Explorer shell identity.","Explicit action; no file/window-state rollback."),
  new ActionDefinition("restart-pc","Windows","shutdown.exe /r /t 0",false,"Exit status confirms request only; boot observed on next launch.","Explicit confirmation; save work first.","shutdown.exe")
 }).ToArray();
 public static ActionDefinition Action(string id)=>Actions.SingleOrDefault(a=>a.Id==id)??throw new InvalidDataException("Unknown registered action.");
 public static IEnumerable<SystemTask> TasksFor(string page)=>SystemTasks.Where(t=>page=="Diagnostics"&&t.Id!="dns-flush"||page=="Storage"&&t.Id is "trim-check" or "drive-analyze" or "drive-optimize" or "disk-scan" or "component-analyze" or "component-clean"||page=="Cleanup"&&t.Id is "component-analyze" or "component-clean");
}
