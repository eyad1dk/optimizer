using System.IO;
using System.Text.Json;
using Microsoft.Win32;
namespace ForgePC;

// Explicit integration switch only. It is not part of normal smoke tests.
internal static class NativeAuditValidation
{
 public static async Task Run(string output)
 {
  var root=Path.Combine(Path.GetTempPath(),"EZoptimizer-native-audit-"+Guid.NewGuid().ToString("N"));
  var settings=new WindowsSettings();var engine=new TuningEngine(settings,new JournalStore(Path.Combine(root,"history")),new WindowsCapabilityService(settings));
  var results=new List<object>();
  try{
   var original=settings.Read("menus");var target=original=="On"?"Off":"On";
   var result=await engine.ApplyAsync("Audit menu preference",[new("menus","Menu animation",original,target)]);
   if(!result.Success||settings.Read("menus")!=target)throw new IOException("Native menu apply failed.");
   if((await engine.RestoreAsync(result.Journal)).Count!=0||settings.Read("menus")!=original)throw new IOException("Menu restoration failed.");
   results.Add(new{Action="Menu animation change / readback / restore",Passed=true});
   var plan=settings.Read("power");var other=settings.Plans().FirstOrDefault(p=>p.Id=="381b4222-f694-41f0-9685-ff5bb260df2e"&&p.Id!=plan);
   if(other!=null){result=await engine.ApplyAsync("Audit installed power plan",[new("power","Installed plan",plan,other.Id)]);if(!result.Success||settings.Read("power")!=other.Id)throw new IOException("Power plan switch failed.");if((await engine.RestoreAsync(result.Journal)).Count!=0||settings.Read("power")!=plan)throw new IOException("Power plan restore failed.");results.Add(new{Action="Installed plan switch / readback / restore",Passed=true});}
   else results.Add(new{Action="Installed plan switch",Skipped="No different Balanced plan installed; no custom scheme is modified."});
   var name="EZoptimizerAudit_"+Guid.NewGuid().ToString("N");const string path=@"Software\Microsoft\Windows\CurrentVersion\Run";
   using(var key=Registry.CurrentUser.CreateSubKey(path,true)){
    // A benign disposable registration; never select an existing startup app.
    key.SetValue(name,"audit-fixture-do-not-launch.exe",RegistryValueKind.String);key.Flush();
    try{var manager=new StartupManager(Path.Combine(root,"startup"));var entry=manager.Read().Single(e=>e.Name==name);manager.Disable(entry);if(key.GetValueNames().Contains(name))throw new IOException("Startup fixture disable failed.");manager.Restore(manager.History().Single().Id);if((string?)key.GetValue(name)!="audit-fixture-do-not-launch.exe")throw new IOException("Startup fixture restore failed.");results.Add(new{Action="Owned startup fixture disable / enable / exact restore",Passed=true});}
    finally{if((string?)key.GetValue(name)=="audit-fixture-do-not-launch.exe")key.DeleteValue(name);key.Flush();}
   }
   await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{Results=results,Host=Environment.OSVersion.VersionString,Administrator=SystemCommands.IsAdministrator,RecoveryDirectory=root,Limitations="No DNS server change, service stop/start, Game Mode registry hack, pagefile edit, Explorer/PC restart, or host cache deletion. Those are not claimed tested."},new JsonSerializerOptions{WriteIndented=true}));
  }finally{foreach(var journal in engine.History().Where(j=>j.IsActive)){var issues=await engine.RestoreAsync(journal);if(issues.Count>0)throw new IOException("Native audit recovery needs review at "+root+": "+string.Join("; ",issues));}}
 }
}
