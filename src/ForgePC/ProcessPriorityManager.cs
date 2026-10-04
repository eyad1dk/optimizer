using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace ForgePC;
public record PriorityRecovery(string Id,int ProcessId,long StartTicks,string Original,string Target,string State);
public interface IProcessPriorityBackend{string? Read(int id,long startTicks);void Write(int id,long startTicks,string expected,string value);}
public sealed class WindowsProcessPriorityBackend : IProcessPriorityBackend
{
 private static Process? Find(int id,long ticks){try{var process=Process.GetProcessById(id);_ = process.Handle;if(process.HasExited||process.StartTime.ToUniversalTime().Ticks!=ticks){process.Dispose();return null;}return process;}catch(ArgumentException){return null;}}
 public string? Read(int id,long ticks){using var process=Find(id,ticks);return process?.PriorityClass.ToString();}
 public void Write(int id,long ticks,string expected,string value){using var process=Find(id,ticks)??throw new InvalidOperationException("The selected process ended or changed identity.");if(process.PriorityClass.ToString()!=expected)throw new InvalidOperationException("Process priority changed outside EZoptimizer.");process.PriorityClass=Enum.Parse<ProcessPriorityClass>(value);if(process.PriorityClass.ToString()!=value)throw new IOException("Process priority verification failed.");}
}
public sealed class ProcessPriorityManager(string directory,IProcessPriorityBackend? processBackend=null)
{
 private readonly IProcessPriorityBackend backend=processBackend??new WindowsProcessPriorityBackend();
 public static readonly string[] Targets=["BelowNormal","Normal","AboveNormal"];
 public static void Validate(PriorityRecovery record){if(!Guid.TryParseExact(record.Id,"N",out _)||record.ProcessId<=0||record.StartTicks<=0||record.Original is not("Idle" or "BelowNormal" or "Normal" or "AboveNormal" or "High")||!Targets.Contains(record.Target)||record.State is not("prepared" or "applied" or "restored" or "ended"))throw new InvalidDataException("Unsupported priority recovery record.");}
 public List<PriorityRecovery> History(){if(!Directory.Exists(directory))return [];return Directory.EnumerateFiles(directory,"*.json").Take(1000).Select(file=>{if(new FileInfo(file).Length>4096)throw new InvalidDataException("Priority record exceeds its limit.");var record=JsonSerializer.Deserialize<PriorityRecovery>(File.ReadAllText(file))??throw new InvalidDataException("Invalid priority record.");Validate(record);if(Path.GetFileNameWithoutExtension(file)!=record.Id)throw new InvalidDataException("Priority identity mismatch.");return record;}).ToList();}
 public void Apply(ProcessEntry entry,string target)
 {
  if(!Targets.Contains(target))throw new InvalidDataException("Choose a supported non-realtime priority.");if(History().Any(r=>r.State is "prepared" or "applied"&&r.ProcessId==entry.Id&&r.StartTicks==entry.StartTicks))throw new InvalidOperationException("Restore the existing priority session first.");
  var original=backend.Read(entry.Id,entry.StartTicks)??throw new InvalidOperationException("Selected process ended.");var record=new PriorityRecovery(Guid.NewGuid().ToString("N"),entry.Id,entry.StartTicks,original,target,"prepared");Save(record);backend.Write(entry.Id,entry.StartTicks,original,target);if(backend.Read(entry.Id,entry.StartTicks)!=target)throw new IOException("Priority verification failed.");Save(record with{State="applied"});
 }
 public void RestoreAll()
 {
  var failures=new List<string>();foreach(var record in History().Where(r=>r.State is "prepared" or "applied")){try{var current=backend.Read(record.ProcessId,record.StartTicks);if(current==null){Save(record with{State="ended"});continue;}if(current!=record.Target&&current!=record.Original)throw new InvalidOperationException("External priority preserved.");if(current!=record.Original)backend.Write(record.ProcessId,record.StartTicks,current,record.Original);if(backend.Read(record.ProcessId,record.StartTicks)!=record.Original)throw new IOException("Priority undo failed.");Save(record with{State="restored"});}catch(Exception error){failures.Add(error.GetType().Name);}}
  if(failures.Count>0)throw new InvalidOperationException($"{failures.Count} process-priority sessions need review. External values were preserved; retry while the original process is running.");
 }
 private void Save(PriorityRecovery record){Validate(record);Directory.CreateDirectory(directory);var path=Path.Combine(directory,record.Id+".json");using(var stream=new FileStream(path+".tmp",FileMode.Create,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)){JsonSerializer.Serialize(stream,record);stream.Flush(true);}File.Move(path+".tmp",path,true);}
}
