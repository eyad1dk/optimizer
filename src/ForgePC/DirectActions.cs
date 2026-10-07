using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;

namespace ForgePC;

public sealed class CacheRow(CacheSpec spec):Observable
{
 public CacheSpec Spec{get;}=spec;public string Name=>Spec.Name;public string Note=>Spec.Note;public string Shield=>Spec.Administrator?"🛡":"";
 private bool selected;private string state="Not scanned",button="Clean",detail="";
 public bool Selected{get=>selected;set=>Set(ref selected,value);}public string State{get=>state;set=>Set(ref state,value);}public string Button{get=>button;set=>Set(ref button,value);}public string Detail{get=>detail;set=>Set(ref detail,value);}public long? Bytes{get;set;}
}
public sealed class DirectActions:Observable
{
 private readonly LocalLogger logger;private bool busy;private string details="No command has run.",status="Ready",dnsLabel="🛡 Flush DNS",explorerLabel="Restart Explorer";
 public Func<bool> ExternalBusy{get;set;}=()=>false;
 public ObservableCollection<CacheRow> Caches{get;}=new(CacheCatalog.All.Select(s=>new CacheRow(s)));
 public ObservableCollection<string> Recent{get;}=[];
 public bool Busy{get=>busy;private set{Set(ref busy,value);RefreshAvailability();}}
 public string Details{get=>details;private set=>Set(ref details,value);}public string Status{get=>status;private set=>Set(ref status,value);}public string DnsLabel{get=>dnsLabel;private set=>Set(ref dnsLabel,value);}public string ExplorerLabel{get=>explorerLabel;private set=>Set(ref explorerLabel,value);}
 public string Total=>$"Eligible size: {Caches.Where(c=>c.Bytes.HasValue).Sum(c=>c.Bytes!.Value)/1048576d:0.0} MB · {Caches.Count(c=>c.Bytes.HasValue)}/{Caches.Count} categories scanned";
 public CacheRow ShaderCache=>Caches.Single(c=>c.Spec.Id=="shader-cache");
 public Command Scan{get;}public Command Clean{get;}public Command CleanSelected{get;}public Command CleanTemp{get;}public Command FlushDns{get;}public Command RestartExplorer{get;}
 private readonly List<Command> commands=[];
 public DirectActions(LocalLogger logger)
 {
  this.logger=logger;
  Command Make(Func<object?,Task> run){var c=new Command(run,Failure,()=>!Busy&&!ExternalBusy());commands.Add(c);return c;}
  Scan=Make(async _=>{Busy=true;Status="Scanning caches…";try{foreach(var row in Caches){try{await ExecuteCache(row,false);}catch(Exception e){row.State="Scan failed · "+e.Message;row.Bytes=null;logger.Error(e);}}Status="Scan finished. Review each row; protected scans can request administrator access.";Raise(nameof(Total));}finally{Busy=false;}});
  Clean=Make(async p=>{if(p is not CacheRow row)throw new InvalidOperationException("Select a cleanup category.");await CleanRows([row]);});
  CleanSelected=Make(async _=>{var rows=Caches.Where(c=>c.Selected).ToArray();if(rows.Length==0)throw new InvalidOperationException("Select at least one cleanup category.");await CleanRows(rows);});
  CleanTemp=Make(_=>CleanRows([Caches.Single(c=>c.Spec.Id=="user-temp")]));
  FlushDns=Make(async _=>{Busy=true;DnsLabel="Flushing…";Status="Flushing DNS…";Details="Command: ipconfig /flushdns\n";try{var code=await SystemCommands.RunAsync("dns-flush",text=>Application.Current.Dispatcher.Invoke(()=>Append(text)));if(code!=0)throw new InvalidOperationException("DNS flush failed (exit "+code+"). See Details.");Status="✓ Windows reports DNS cache flushed. New lookups may immediately repopulate it.";Remember(Status);DnsLabel="✓ Flushed";}catch{DnsLabel="Retry DNS flush";throw;}finally{Busy=false;}});
  RestartExplorer=Make(async _=>{Busy=true;ExplorerLabel="Restarting…";Status="Restarting the desktop shell…";Details="Native action: restart the current session's verified Windows Explorer shell. Folder windows may close.\n";try{await ShellRestart.ExecuteAsync();Status="✓ New Explorer shell detected.";Append(Status);Remember(Status);ExplorerLabel="✓ Restarted";}catch{ExplorerLabel="Retry restart";throw;}finally{Busy=false;}});
 }
 public void RefreshAvailability(){foreach(var c in commands)c.Refresh();}
 private void Append(string text){Details+=text;if(Details.Length>65536)Details="[Earlier output omitted]\n"+Details[^60000..];}
 private async Task CleanRows(CacheRow[] rows)
 {Busy=true;try{int failures=0;foreach(var row in rows){try{await ExecuteCache(row,true);}catch(Exception e){failures++;row.State="× Failed · "+e.Message;row.Button="Retry clean";logger.Error(e);Append("\n"+row.Name+": "+e.Message);}}Status=failures==0?"Cleanup finished. See removed, skipped and remaining counts per row.":$"Cleanup finished with {failures} failed categories; other selected categories were attempted.";}finally{Busy=false;}}
 private async Task ExecuteCache(CacheRow row,bool clean)
 {
  row.Button=clean?"Cleaning…":"Scanning…";row.State=row.Button;Status=row.Name+" · "+row.Button;
  try{var result=await SystemCommands.CleanupAsync(row.Spec.Id,clean);row.Bytes=result.BytesAfter;row.State=clean?$"{result.FilesRemoved} removed · {result.BytesRemoved/1048576d:0.0} MB logical bytes · {result.LockedFiles} locked · {result.FilesSkipped} total skipped · {result.FilesAfter} eligible remain":$"{result.FilesBefore} eligible files · {result.LockedFiles} locked · {result.BytesBefore/1048576d:0.0} MB"+(result.Partial?" · partial scan":"");row.Detail=JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true});Details=row.Name+"\nNative handle-based cleanup; no script.\n"+row.Detail;row.Button=clean?(result.FilesBefore==0?"No files":result.Success?"✓ Cleaned":"Clean remaining"):"Clean";if(clean){Remember(row.Name+": "+result.Message);logger.Write("cleanup",row.Spec.Id,row.State);}Raise(nameof(Total));}
  catch{row.Button="Retry";throw;}
 }
 public void Remember(string text){Recent.Insert(0,DateTime.Now.ToString("HH:mm")+"  "+text);while(Recent.Count>20)Recent.RemoveAt(Recent.Count-1);}
 private void Failure(Exception e){logger.Error(e);Status="× "+(e is System.ComponentModel.Win32Exception w&&w.NativeErrorCode==1223?"Administrator permission cancelled.":e.Message);Append("\n"+Status);}
}

internal static class ShellRestart
{
 [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
 [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
 public static async Task ExecuteAsync()
 {
  var shell=GetShellWindow();if(shell==IntPtr.Zero)throw new InvalidOperationException("No desktop shell found.");GetWindowThreadProcessId(shell,out uint pid);
  using var process=Process.GetProcessById(checked((int)pid));using var own=Process.GetCurrentProcess();var expected=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe");
  if(process.SessionId!=own.SessionId||!string.Equals(process.MainModule?.FileName,expected,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("The shell identity could not be verified.");
  // The open process handle pins identity; only this shell process is terminated, never a name-wide taskkill.
  _=process.Handle;process.Kill();await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
  await Task.Delay(1000);if(GetShellWindow()==IntPtr.Zero)using(Process.Start(new ProcessStartInfo(expected){UseShellExecute=true})){}
  for(int i=0;i<40;i++){var next=GetShellWindow();if(next!=IntPtr.Zero){GetWindowThreadProcessId(next,out uint nextPid);if(nextPid!=pid){using var replacement=Process.GetProcessById((int)nextPid);if(replacement.SessionId==own.SessionId&&string.Equals(replacement.MainModule?.FileName,expected,StringComparison.OrdinalIgnoreCase))return;}}await Task.Delay(250);}
  throw new IOException("A replacement Explorer shell was not verified. Open Task Manager and run explorer.exe if the taskbar is absent.");
 }
}
