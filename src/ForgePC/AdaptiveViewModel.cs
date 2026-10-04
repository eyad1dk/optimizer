using System.Text.Json;
namespace ForgePC;

public sealed partial class MainViewModel
{
 private string lastAdapter="",maintenanceOutput="No maintenance action has run.";
 public string MaintenanceOutput{get=>maintenanceOutput;private set=>Set(ref maintenanceOutput,value);}
 public Command RunMaintenance{get;private set;}=null!;
 private HardwareSnapshot? hardwareSnapshot;
 private IReadOnlyList<GraphicsAdapter> graphicsAdapters=[];
 private IReadOnlyList<Recommendation> recommendations=[];
 private string hardwareError="Scan PC to inspect hardware.",focus="Balanced",customDns="",customPagefile="C:\\pagefile.sys",pageMinimum="2048",pageMaximum="8192";
 private bool noRecording;
 public string[] FocusChoices=>RecommendationEngine.Focuses;
 public string OptimizationFocus{get=>focus;set{if(FocusChoices.Contains(value)){Set(ref focus,value);Recalculate();}}}
 public bool NoRecording{get=>noRecording;set{Set(ref noRecording,value);Recalculate();}}
 public string CustomDns{get=>customDns;set=>Set(ref customDns,value);}
 public string PagefilePath{get=>customPagefile;set=>Set(ref customPagefile,value);}
 public string PagefileMinimum{get=>pageMinimum;set=>Set(ref pageMinimum,value);}
 public string PagefileMaximum{get=>pageMaximum;set=>Set(ref pageMaximum,value);}
 public Command ApplyRecommended{get;private set;}=null!;
 public Command ApplyCustomDns{get;private set;}=null!;
 public Command ApplyCustomPagefile{get;private set;}=null!;
 private IEnumerable<Recommendation> PageRecommendations=>recommendations.Where(r=>Page is "Optimize" or "Profiles"||r.Category==Page||Page=="GPU"&&r.Id=="hw:policy:dvr"||Page=="Gaming"&&r.Id=="power");
 public string RecommendationSummary=>hardwareSnapshot==null?hardwareError:$"Recommended for this PC · {OptimizationFocus}\n{PageRecommendations.Count(r=>!r.Optimized)} recommended changes · {PageRecommendations.Count(r=>r.Optimized)} already optimized"+(PageRecommendations.Any()?"":"\nNo sufficiently supported automatic recommendation for this section.");
 public string RecommendationCounts=>string.Join(" · ",recommendations.GroupBy(r=>r.Category).Select(g=>$"{g.Key}: {g.Count(r=>!r.Optimized)}"))+"\n"+recommendations.Count(r=>!r.Optimized)+" unique recommended changes; no score or performance gain is estimated.";
 private void InitializeAdaptive(Func<Func<object?,Task>,Func<bool>?,Command> make)
 {
  bool Available()=>!Busy&&!Tools.Busy&&!Actions.Busy&&!recoveryOnly;
  RunMaintenance=make(async p=>{if(p is not string id)return;Busy=true;try{
   if(id=="restore-network"){foreach(var journal in History.Where(j=>j.IsActive).ToArray()){var ids=journal.Operations.Where(o=>Catalog.Get(o.Id).Category=="Network").Select(o=>o.Id).ToHashSet();if(ids.Count>0){var errors=await Task.Run(()=>engine.RestoreSelectedAsync(journal,ids));if(errors.Count>0)throw new InvalidOperationException(string.Join("; ",errors));}}await ReloadHistory();await RefreshSettings();MaintenanceOutput="Recorded original network settings verified. DHCP leases and external changes are not reset.";return;}
   if(id=="gateway"){await RefreshSettings();if(hardwareSnapshot?.Network.ValueKind!=JsonValueKind.Object||!hardwareSnapshot.Network.TryGetProperty("Gateway",out var gateway)||gateway.GetArrayLength()==0)throw new InvalidOperationException("No active IPv4 gateway was detected.");Endpoint=gateway[0].GetString()!;var result=await new NetworkDiagnostics().MeasureAsync(Endpoint);MaintenanceOutput=JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true});return;}
   if(id is "bin-scan" or "bin-clean"){if(id=="bin-clean"&&!ui.Confirm("Permanently empty this user's Recycle Bin on all drives? Files cannot be restored by EZoptimizer.","Empty Recycle Bin"))return;MaintenanceOutput=await Task.Run(()=>{if(id=="bin-clean")return Maintenance.EmptyBin();var bin=Maintenance.ReadBin();return $"Recycle Bin: {bin.Items} items · {bin.Bytes/1048576d:0.0} MiB. Nothing deleted.";});return;}
   string argument=id=="dns-test"?NetworkDiagnostics.ValidateEndpoint(Endpoint):"";
   if(id is "renew" or "release"){await RefreshSettings();if(lastAdapter.Length==0)throw new InvalidOperationException("No adapter identity has been detected. Refresh Network first.");argument=lastAdapter;}
   if(id is "renew" or "release" or "delivery-clean"){var warning=id=="release"?"Release the last detected adapter's DHCP lease? This disconnects IPv4 traffic. Use Renew DHCP to request a new lease; the exact old address cannot be restored.":id=="renew"?"Renew the last detected adapter's DHCP lease? Connections may be interrupted. The DHCP server chooses the address.":"Permanently remove Delivery Optimization cache through Windows? Updates may need to download content again. Exact recovered bytes are unavailable; no app rollback.";if(!ui.Confirm(warning,"Review maintenance action"))return;}
   MaintenanceOutput="Running "+id+"…";MaintenanceOutput=await Maintenance.Run(id,argument);if(id is "renew" or "release")await RefreshSettings();
  }catch(Exception error){MaintenanceOutput="Failed: "+error.Message;throw;}finally{Status=MaintenanceOutput.Split('\n')[0];Busy=false;}},Available);
  ApplyRecommended=make(_=>ApplyRecommendations(Page),Available);
  OptimizeMyPc=make(_=>ApplyRecommendations(null),Available);
  ApplyCustomDns=make(async _=>{var item=Operations.SingleOrDefault(o=>o.Eligible&&o.Id.StartsWith("hw:net:dns:"))??throw new InvalidOperationException("Refresh Network first; no active IPv4 DNS control is available.");item.Target=HardwareBackend.CustomDns(CustomDns);await ApplySingle(item);},Available);
  ApplyCustomPagefile=make(async _=>{var item=Operations.SingleOrDefault(o=>o.Eligible&&o.Id=="hw:pagefile")??throw new InvalidOperationException("Refresh Memory first; pagefile configuration is unavailable.");if(!System.Text.RegularExpressions.Regex.IsMatch(PagefilePath,@"^[A-Za-z]:\\pagefile\.sys$")||!uint.TryParse(PagefileMinimum,out var min)||!uint.TryParse(PagefileMaximum,out var max)||min>max||max>1048576||max==0)throw new InvalidOperationException("Use a local drive pagefile.sys path and sizes in MiB (maximum at least minimum, 1–1048576).");Busy=true;try{var original=await Task.Run(()=>HardwareBackend.Read(item.Id));using var doc=JsonDocument.Parse(original.Value);var files=doc.RootElement.GetProperty("Files").EnumerateArray().Where(f=>!f.GetProperty("Name").GetString()!.Equals(PagefilePath,StringComparison.OrdinalIgnoreCase)).Select(f=>new {Name=f.GetProperty("Name").GetString()!,InitialSize=f.GetProperty("InitialSize").GetUInt32(),MaximumSize=f.GetProperty("MaximumSize").GetUInt32()}).ToList();files.Add(new{Name=char.ToUpperInvariant(PagefilePath[0])+PagefilePath[1..],InitialSize=min,MaximumSize=max});item.Target=JsonSerializer.Serialize(new{Automatic=false,Files=files.OrderBy(f=>f.Name,StringComparer.OrdinalIgnoreCase).ToArray()});await ApplySingle(item);}finally{Busy=false;}},Available);
  PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(Page)){Raise(nameof(RecommendationSummary));Raise(nameof(RecommendationCounts));}};
 }
 private async Task RefreshHardware()
 {
  try{
   var snapshot=await Task.Run(HardwareBackend.Inspect);try{graphicsAdapters=await Task.Run(GraphicsInventory.Read);}catch(Exception error){graphicsAdapters=[];logger.Error(error);}hardwareSnapshot=snapshot;hardwareError="";if(snapshot.Network.ValueKind==JsonValueKind.Object&&snapshot.Network.TryGetProperty("Guid",out var adapter))lastAdapter=adapter.GetString()??"";
   var detected=snapshot.Controls.Select(c=>c.Id).ToHashSet();
   foreach(var old in Operations.Where(o=>HardwareBackend.Contains(o.Id)&&!detected.Contains(o.Id))){old.Eligible=false;old.Compatibility="Not present or no longer readable on this scan.";old.Current="Unavailable";old.Detect("Unavailable");}
   foreach(var c in snapshot.Controls){var item=Operations.SingleOrDefault(o=>o.Id==c.Id);if(item==null){item=new(HardwareBackend.Definition(c.Id));Operations.Add(item);}item.Hardware=c;Replace(item.Choices,c.Choices.DistinctBy(v=>v.Value));item.Detect(c.Value);item.Current=c.Format(c.Value);item.Target=c.Value;item.Eligible=c.Supported;item.Compatibility="Detected on this PC; fresh validation before every write.";item.CanRestore=History.Any(j=>j.IsActive&&j.Operations.Any(o=>o.Id==c.Id&&o.State!="rolled back"));}
  }catch(Exception error){hardwareSnapshot=null;hardwareError="Hardware scan failed: "+error.Message;logger.Error(error);foreach(var item in Operations.Where(o=>HardwareBackend.Contains(o.Id))){item.Eligible=false;item.Current="Unavailable";item.Compatibility=hardwareError;}}
  Recalculate();
 }
 private void Recalculate()
 {
  recommendations=hardwareSnapshot==null?[]:RecommendationEngine.Calculate(hardwareSnapshot,Operations.ToDictionary(o=>o.Id,o=>o.Raw),Operations.Where(o=>o.Eligible).Select(o=>o.Id).ToHashSet(),Plans.Select(p=>p.Id).ToHashSet(),WindowsSystemProbe.BatteryState(),OptimizationFocus,NoRecording);
  foreach(var item in Operations){var rule=recommendations.SingleOrDefault(r=>r.Id==item.Id);item.Badge=rule==null?"":rule.Optimized?"✓ OPTIMIZED":"RECOMMENDED";item.RecommendationReason=rule?.Reason??"";}
  Raise(nameof(RecommendationSummary));Raise(nameof(RecommendationCounts));Raise(nameof(PageOperations));Raise(nameof(ProfileRows));
 }
 private string HardwareInformation(string page)
 {
  if(hardwareSnapshot is not {} h)return hardwareError;
  var json=new JsonSerializerOptions{WriteIndented=true};object information=page switch{"CPU"=>new{h.Cpu,h.Laptop,OnBattery=WindowsSystemProbe.BatteryState(),PowerPlan=CurrentPowerPlan},"GPU"=>(object)new{Adapters=graphicsAdapters,h.Graphics,LiveActivity=GpuUsage},"Memory"=>new{h.TotalMemory,h.FreeMemory,h.Pagefiles},"Storage"=>new{h.Disks,h.Volumes},"Network"=>new{h.Network,h.Tcp},_=>new{h.OS,h.Build,h.UptimeSeconds}};
  return JsonSerializer.Serialize(information,json)+"\n\n"+RecommendationSummary+(h.Errors.Length>0?"\n\nUnavailable probes (not applied):\n"+string.Join("\n",h.Errors):"");
 }
 private async Task ApplyRecommendations(string? section)
 {
  Busy=true;
  try{
   Status="Reading hardware, capabilities and current Windows settings…";await RefreshSettings();if(hardwareSnapshot==null)throw new InvalidOperationException(hardwareError);
   var selected=recommendations.Where(r=>section==null||section is "Optimize" or "Profiles"||r.Category==section||section=="GPU"&&r.Id=="hw:policy:dvr"||section=="Gaming"&&r.Id=="power").DistinctBy(r=>r.Id).ToArray();
   var changes=selected.Where(r=>!r.Optimized).ToArray();if(changes.Length==0){Status=$"0 applied · {selected.Count(r=>r.Optimized)} already optimized · 0 failed. No eligible change is needed.";return;}
   var plan=await Task.Run(()=>new ExecutionPlanner(settings,capabilities).Preview(changes.Select(r=>new KeyValuePair<string,string>(r.Id,r.Target))));
   if(plan.Count==0){Status="Fresh Windows reads already match all recommendations.";return;}
   if(!ui.Confirm(string.Join("\n\n",plan.Select(p=>Catalog.Get(p.Id).Title+": "+Friendly(p.Id,p.Before)+" → "+Friendly(p.Id,p.Target)+"\n"+changes.Single(r=>r.Id==p.Id).Reason))+"\n\nOriginals are saved before changes. Existing active changes are preserved; restore conflicting settings first.","Apply "+OptimizationFocus+" recommendations")){Status="Cancelled; no settings changed.";return;}
   var progress=new Progress<OperationProgress>(p=>Status=$"{Catalog.Get(p.OperationId).Category} · {p.Completed}/{p.Total} · {Catalog.Get(p.OperationId).Title} · {p.State}");
   var result=await Task.Run(()=>engine.ApplyAsync("Recommended · "+OptimizationFocus,plan.Select(p=>new Change(p.Id,p.Title,p.Before,p.Target)).ToList(),progress:progress,allowIndependent:true));
   await ReloadHistory();await RefreshSettings();
   var verified=result.Journal.Operations.Count(o=>o.State=="applied");Status=$"{verified} applied and verified · {selected.Count(r=>r.Optimized)} already optimized · "+(result.Success?"0 failed":$"{result.Journal.Operations.Count(o=>o.Result.StartsWith("Apply failed"))} failed; batch recovery attempted")+". "+result.Message;
  }finally{Busy=false;}
 }
}
