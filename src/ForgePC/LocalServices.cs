using System.Diagnostics;
using System.IO;
using System.Management;
using System.Security.Principal;
using System.Text.Json;
namespace ForgePC;
public sealed class LocalLogger(string directory)
{
 private readonly object gate=new();
 public void Write(string session,string operation,string result,long duration=0)
 {
  lock(gate) try
  {
   Directory.CreateDirectory(directory); var path=Path.Combine(directory,"events.jsonl");
   if(File.Exists(path) && new FileInfo(path).Length>1048576)
   {
    for(var i=2;i>=1;i--){ var old=path+"."+i; if(File.Exists(old))File.Move(old,path+"."+(i+1),true); }
    File.Move(path,path+".1",true);
   }
   File.AppendAllText(path,JsonSerializer.Serialize(new { At=DateTimeOffset.UtcNow,Session=session,Operation=operation,Result=result,DurationMs=duration })+Environment.NewLine);
  } catch(Exception error) { Trace.TraceWarning("Local log write failed: "+error.GetType().Name); /* Journals remain authoritative. */ }
 }
 public string Error(Exception e)
 { var id=Guid.NewGuid().ToString("N")[..8]; Write(id,"error",e.GetType().Name+":"+e.HResult.ToString("X8")); return id; }
}
public record RestorePointResult(string State,string Message);
public interface IRestorePointService
{ Task<RestorePointResult> InspectAsync(CancellationToken token=default); Task<RestorePointResult> CreateReviewedAsync(CancellationToken token=default); }
public sealed class RestorePointService : IRestorePointService
{
 public async Task<RestorePointResult> InspectAsync(CancellationToken token=default)
 {
  try
  {
   var points=await Task.Run(()=>WindowsSystemProbe.Wmi(@"root\default","SELECT SequenceNumber,CreationTime FROM SystemRestore","SequenceNumber","CreationTime"),token).WaitAsync(TimeSpan.FromSeconds(8),token);
   return new("Available",points.Count>0?$"Provider accessible; {points.Count} retained restore points. Protection coverage still depends on Windows drive settings.":"Provider accessible; no retained restore points. Windows may require System Protection to be enabled manually.");
  }
  catch(UnauthorizedAccessException) { return new("Denied","Windows restricted restore-point inspection. Exact per-setting journals still protect eligible controls."); }
  catch { return new("Unknown or unavailable","System Protection could not be verified. Open Windows protection settings. Eligible low-risk controls use exact journals, not a claimed restore point."); }
 }
 public async Task<RestorePointResult> CreateReviewedAsync(CancellationToken token=default)
 {
  try
  {
   if(SystemCommands.IsAdministrator)return ResultFromCode(await Task.Run(RunDedicatedHelper,token));
   var executable=Environment.ProcessPath ?? throw new IOException();
   if(!Path.GetFileName(executable).Equals("EZoptimizer.exe",StringComparison.OrdinalIgnoreCase))return new("Unavailable","Restore-point creation requires the packaged EZoptimizer executable; this development host cannot request elevation.");
   // The elevated mode accepts one fixed switch, no command, path, profile or IPC payload.
   using var helper=Process.Start(new ProcessStartInfo(executable,"--create-restore-point") { UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden });
   if(helper==null)return new("Failed","Could not start the dedicated restore-point operation.");
   await helper.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(90),token);
   return ResultFromCode(helper.ExitCode);
  }
  catch(System.ComponentModel.Win32Exception e) when(e.NativeErrorCode==1223) { return new("Denied","UAC was declined. No restore-point success is claimed."); }
  catch(TimeoutException) { return new("Unverified","Windows has not returned a verified result. Check Windows protection tools before trying again."); }
  catch(OperationCanceledException) { return new("Unverified","Waiting was cancelled; inspect Windows restore points before retrying."); }
  catch { return new("Failed","Restore-point creation could not be verified."); }
 }
 public static RestorePointResult ResultFromCode(int code)=>code switch
 {
  0=>new("Created","A new EZoptimizer restore point was verified. This supplements exact undo and is not a backup of personal files."),
  2=>new("Skipped","Windows did not create a new point; frequency limits or protection configuration may apply. No global throttling was changed."),
  3=>new("Unavailable","System Protection is unavailable or disabled. Open its Windows settings; EZoptimizer does not enable it."),
  4=>new("Denied","Privileges or policy denied this operation."),
  _=>new("Failed","Windows could not verify a newly created restore point.")
 };
 public static int RunDedicatedHelper()
 {
  if(!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))return 4;
  try
  {
   var before=WindowsSystemProbe.Wmi(@"root\default","SELECT SequenceNumber FROM SystemRestore","SequenceNumber").Select(r=>Convert.ToInt64(r["SequenceNumber"])).ToHashSet();
   using var type=new ManagementClass(new ManagementScope(@"root\default"),new ManagementPath("SystemRestore"),null);
   using var parameters=type.GetMethodParameters("CreateRestorePoint");
   var label="EZoptimizer "+DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
   parameters["Description"]=label; parameters["RestorePointType"]=12; parameters["EventType"]=100;
   using var output=type.InvokeMethod("CreateRestorePoint",parameters,new InvokeMethodOptions { Timeout=TimeSpan.FromSeconds(60) });
   if(output?["ReturnValue"]==null)return 5;
   var code=Convert.ToUInt32(output?["ReturnValue"]);
   if(code!=0)return code is 5 or 0x80070005 ? 4 : code==1058 ? 3 : 5;
   var after=WindowsSystemProbe.Wmi(@"root\default","SELECT SequenceNumber,Description FROM SystemRestore","SequenceNumber","Description");
   return after.Any(r=>!before.Contains(Convert.ToInt64(r["SequenceNumber"])) && r["Description"]?.ToString()==label)?0:2;
  }
  catch(UnauthorizedAccessException){return 4;} catch(ManagementException){return 3;} catch{return 5;}
 }
}
public static class ReportService
{
 public static string Serialize(SystemReading? reading,HardwareInventory? inventory,IEnumerable<Journal> history)
 {
  // Explicit allowlist; network identifiers, startup/process names, journal values,
  // command lines, paths, free text workload context and imported profile names are excluded.
  return JsonSerializer.Serialize(new {
   App="EZoptimizer",Version="2.0.0",GeneratedUtc=DateTimeOffset.UtcNow,
   OS=inventory?.OS,CpuModel=inventory?.Cpu,inventory?.LogicalProcessors,inventory?.Architecture,
   LatestSample=reading==null?null:new { reading.At,reading.Cpu,reading.MemoryPercent,reading.TotalMemory,reading.FreeMemory,reading.DiskTotal,reading.DiskFree,reading.OnBattery,reading.Status,reading.DurationMs },
   Recovery=history.Select(j=>new { j.Id,j.SchemaVersion,j.CreatedUtc,j.Status,Operations=j.Operations.Select(o=>new { o.Id,o.Version,o.State,o.DurationMs }) })
  },new JsonSerializerOptions { WriteIndented=true });
 }
}
