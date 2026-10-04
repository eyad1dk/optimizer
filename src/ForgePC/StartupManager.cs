using Microsoft.Win32;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForgePC;

public record StartupRegistration(string Name,string Value,RegistryValueKind Kind,string State,string? RecoveryId=null)
{public string Display=>$"{Name} · {State}";public bool CanDisable=>RecoveryId==null;public bool CanRestore=>RecoveryId!=null;}
public record StartupRecovery(int Schema,string Id,string Name,string Original,RegistryValueKind Kind,string State);
public interface IStartupRegistry
{
 List<StartupRegistration> Entries(); void DeleteIfUnchanged(StartupRegistration entry); void RestoreIfMissing(StartupRecovery recovery);
}
public sealed class WindowsStartupRegistry : IStartupRegistry
{
 private const string DisabledKey=@"Software\EZoptimizer\DisabledRun";
 private const string Key=@"Software\Microsoft\Windows\CurrentVersion\Run";
 public List<StartupRegistration> Entries(){var result=new List<StartupRegistration>();using var key=Registry.CurrentUser.OpenSubKey(Key);if(key!=null)foreach(var name in key.GetValueNames()){var kind=key.GetValueKind(name);if(kind is RegistryValueKind.String or RegistryValueKind.ExpandString&&key.GetValue(name,null,RegistryValueOptions.DoNotExpandEnvironmentNames) is string value)result.Add(new(name,value,kind,"Run registration present"));}return result;}
 private static bool Matches(RegistryKey key,string name,string value,RegistryValueKind kind)=>key.GetValueNames().Contains(name,StringComparer.OrdinalIgnoreCase)&&key.GetValueKind(name)==kind&&Equals(key.GetValue(name,null,RegistryValueOptions.DoNotExpandEnvironmentNames),value);
 public void DeleteIfUnchanged(StartupRegistration entry){using var key=Registry.CurrentUser.OpenSubKey(Key,true)??throw new InvalidOperationException("Startup Run key is unavailable.");if(!Matches(key,entry.Name,entry.Value,entry.Kind))throw new InvalidOperationException("Startup entry changed. Refresh before disabling.");using var disabled=Registry.CurrentUser.CreateSubKey(DisabledKey,true);if(disabled.GetValueNames().Contains(entry.Name,StringComparer.OrdinalIgnoreCase)&&!Matches(disabled,entry.Name,entry.Value,entry.Kind))throw new InvalidOperationException("A different saved startup registration exists; resolve recovery first.");disabled.SetValue(entry.Name,entry.Value,entry.Kind);disabled.Flush();if(!Matches(disabled,entry.Name,entry.Value,entry.Kind))throw new IOException("Startup preservation failed; active entry was not changed.");key.DeleteValue(entry.Name,true);key.Flush();if(key.GetValueNames().Contains(entry.Name,StringComparer.OrdinalIgnoreCase))throw new IOException("Startup disable verification failed.");}
 public void RestoreIfMissing(StartupRecovery recovery){using var key=Registry.CurrentUser.CreateSubKey(Key,true);if(key.GetValueNames().Contains(recovery.Name,StringComparer.OrdinalIgnoreCase)&&!Matches(key,recovery.Name,recovery.Original,recovery.Kind))throw new InvalidOperationException("Startup entry changed outside EZoptimizer; current value preserved.");if(!Matches(key,recovery.Name,recovery.Original,recovery.Kind)){key.SetValue(recovery.Name,recovery.Original,recovery.Kind);key.Flush();}if(!Matches(key,recovery.Name,recovery.Original,recovery.Kind))throw new IOException("Startup restoration could not be verified.");using var disabled=Registry.CurrentUser.OpenSubKey(DisabledKey,true);if(disabled!=null&&Matches(disabled,recovery.Name,recovery.Original,recovery.Kind)){disabled.DeleteValue(recovery.Name,true);disabled.Flush();}}
}
public sealed class StartupManager(string directory,IStartupRegistry? registry=null)
{
 private readonly IStartupRegistry backend=registry??new WindowsStartupRegistry();
 private static readonly JsonSerializerOptions Json=new(){WriteIndented=true,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
 private readonly object gate=new();
 public List<StartupRegistration> Read()
 {
  var items=backend.Entries();
  foreach(var recovery in History().Where(r=>r.State!="restored"))items.Add(new(recovery.Name,recovery.Original,recovery.Kind,"Saved registration · "+recovery.State,recovery.Id));return items.OrderBy(i=>i.Name).ToList();
 }
 public List<StartupRecovery> History()
 {
  if(!Directory.Exists(directory))return [];var result=new List<StartupRecovery>();
  foreach(var file in Directory.EnumerateFiles(directory,"*.json").Take(1000)){if(new FileInfo(file).Length>65536)throw new InvalidDataException("Startup recovery record is too large.");var record=JsonSerializer.Deserialize<StartupRecovery>(File.ReadAllText(file),Json)??throw new InvalidDataException("Invalid startup recovery.");Validate(record);if(Path.GetFileNameWithoutExtension(file)!=record.Id)throw new InvalidDataException("Startup recovery identity mismatch.");result.Add(record);}return result;
 }
 public void Disable(StartupRegistration entry)
 {
  lock(gate){if(entry.RecoveryId!=null)throw new InvalidOperationException("Select a current Run registration.");if(History().Any(r=>r.Name.Equals(entry.Name,StringComparison.OrdinalIgnoreCase)&&r.State!="restored"))throw new InvalidOperationException("Recover the existing entry first.");
   var current=backend.Entries().SingleOrDefault(x=>x.Name.Equals(entry.Name,StringComparison.OrdinalIgnoreCase));if(current==null||current.Value!=entry.Value||current.Kind!=entry.Kind)throw new InvalidOperationException("Startup entry changed. Refresh before disabling.");
   var record=new StartupRecovery(1,Guid.NewGuid().ToString("N"),entry.Name,entry.Value,entry.Kind,"prepared");Save(record);
   backend.DeleteIfUnchanged(entry);if(backend.Entries().Any(x=>x.Name.Equals(entry.Name,StringComparison.OrdinalIgnoreCase)))throw new IOException("Startup removal was not verified.");Save(record with{State="disabled"});
  }
 }
 public void Restore(string id)
 {
  lock(gate){var record=History().SingleOrDefault(r=>r.Id==id)??throw new InvalidDataException("Startup recovery not found.");if(record.State=="restored")return;
   backend.RestoreIfMissing(record);if(!backend.Entries().Any(x=>x.Name.Equals(record.Name,StringComparison.OrdinalIgnoreCase)&&x.Value==record.Original&&x.Kind==record.Kind))throw new IOException("Exact startup restoration was not verified.");Save(record with{State="restored"});
  }
 }
 public static void Validate(StartupRecovery record)
 {if(record.Schema!=1||!Guid.TryParseExact(record.Id,"N",out _)||string.IsNullOrWhiteSpace(record.Name)||record.Name.Length>16383||record.Original==null||record.Original.Length>16384||record.Kind is not(RegistryValueKind.String or RegistryValueKind.ExpandString)||record.State is not("prepared" or "disabled" or "restored"))throw new InvalidDataException("Unsupported startup recovery data.");}
 private void Save(StartupRecovery record)
 {Validate(record);Directory.CreateDirectory(directory);var path=Path.Combine(directory,record.Id+".json");using(var stream=new FileStream(path+".tmp",FileMode.Create,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)){JsonSerializer.Serialize(stream,record,Json);stream.Flush(true);}File.Move(path+".tmp",path,true);}
}
