namespace ForgePC;
public record V2Profile(string Name,int Count);

public sealed partial class MainViewModel
{
 public DirectActions Actions{get;private set;}=null!;
 public Command RestoreOne{get;private set;}=null!;
 public Command ApplyOne{get;private set;}=null!;public Command RefreshControls{get;private set;}=null!;public Command ApplyProfileNow{get;private set;}=null!;public Command HighPerformance{get;private set;}=null!;
 public string AdminStatus=>SystemCommands.IsAdministrator?"● Administrator":"● Standard User";
 public string RestartIndicator=>History.Any(j=>j.IsActive&&j.Operations.Any(o=>o.State=="applied"&&Catalog.Get(o.Id).Restart))?"⚠ Restart may be required":"No restart flagged by EZoptimizer";
 public IEnumerable<OperationItem> PageOperations=>Operations.Where(o=>o.Eligible&&(o.Definition.Category==Page||Page=="GPU"&&o.Id=="hw:policy:dvr"||Page=="CPU"&&o.Id=="power"||Page=="Gaming"&&(o.Id=="power"||o.Id=="hw:policy:throttle"||o.Id==PointerAcceleration.Id)));
 public IEnumerable<V2Profile> ProfileRows=>FocusChoices.Select(p=>new V2Profile(p,hardwareSnapshot==null?0:RecommendationEngine.Calculate(hardwareSnapshot,Operations.ToDictionary(o=>o.Id,o=>o.Raw),Operations.Where(o=>o.Eligible).Select(o=>o.Id).ToHashSet(),Plans.Select(p=>p.Id).ToHashSet(),WindowsSystemProbe.BatteryState(),p,NoRecording).Count));
 public bool HasHighPerformance=>Plans.Any(p=>p.Id=="8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
 private void InitializeV2(Func<Func<object?,Task>,Func<bool>?,Command> make,string dataRoot)
 {
  Actions=new(logger){ExternalBusy=()=>Busy||Tools.Busy};
  Actions.PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(DirectActions.Busy)){RefreshCommands();Tools.RefreshAvailability();Raise(nameof(CanApply));}if(e.PropertyName==nameof(DirectActions.Status))Status=Actions.Status;};
  Tools.PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(FeatureTools.Busy))Actions.RefreshAvailability();};
  PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(Busy))Actions.RefreshAvailability();if(e.PropertyName==nameof(CurrentPowerPlan))Raise(nameof(HasHighPerformance));};
  InitializeAdaptive(make);
  Tools.RefreshHardwareInfo=async ()=>{await RefreshSettings();return HardwareInformation(Tools.Section);};
  bool Available()=>!Busy&&!Tools.Busy&&!Actions.Busy&&!recoveryOnly;
  RefreshControls=make(async _=>{Busy=true;try{await RefreshSettings();Status=RecommendationSummary;}finally{Busy=false;}},Available);
  ApplyOne=make(async p=>{if(p is OperationItem item)await ApplySingle(item);},Available);
  RestoreOne=make(async p=>{if(p is not OperationItem item)return;var journal=History.FirstOrDefault(j=>j.IsActive&&j.Operations.Any(o=>o.Id==item.Id&&o.State!="rolled back"));if(journal==null){Status="No saved original needs restoring.";return;}item.ActionLabel="Restoring…";try{await RestoreSubset(journal,new HashSet<string>{item.Id});item.Result=item.CanRestore?"× Recovery needs review; see Recovery.":"✓ Original value verified";}finally{item.ResetAction();}},Available);
  HighPerformance=make(async _=>{var item=Operations.Single(o=>o.Id=="power");if(!HasHighPerformance)throw new InvalidOperationException("High Performance is not installed on this PC.");item.Target="8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";await ApplySingle(item);},()=>Available()&&HasHighPerformance);
  ApplyProfileNow=make(async p=>{if(p is string selected)OptimizationFocus=selected;await ApplyRecommendations(null);},Available);
 }
 private async Task ApplySingle(OperationItem item)
 {
  var target=item.Target;if(!item.Eligible)throw new InvalidOperationException(item.Compatibility);
  Busy=true;item.ActionLabel="Applying…";item.Result="Reading original…";
  try{
  if(item.IsBinary){var current=await Task.Run(()=>settings.Read(item.Id));target=item.BinaryTarget(current);var previous=History.FirstOrDefault(j=>j.IsActive&&j.Operations.Any(o=>o.Id==item.Id&&o.State!="rolled back"));if(previous!=null){var saved=previous.Operations.Single(o=>o.Id==item.Id);var original=item.Hardware?.Effective(saved.Before)??saved.Before;if(original==(item.Hardware?.Effective(target)??target)){var failures=await Task.Run(()=>engine.RestoreSelectedAsync(previous,new HashSet<string>{item.Id}));item.Result=failures.Count==0?"✓ Original value verified":"× "+string.Join("; ",failures);Status=item.Title+" · "+item.Result;await ReloadHistory();await RefreshSettings();return;}}}
  var plan=await Task.Run(()=>new ExecutionPlanner(settings,capabilities).Preview([new(item.Id,target)]));if(plan.Count==0){item.Result="Already matches Windows.";item.ActionLabel="Apply";Status=item.Result;return;}
  if(!item.Definition.Risk.StartsWith("Low")&&!ui.Confirm(item.Title+"\n"+item.Tradeoff+"\n\n"+Friendly(item.Id,plan[0].Before)+" → "+Friendly(item.Id,target),"Apply setting")){item.Result="Cancelled; no change.";item.ActionLabel="Apply";return;}

  var result=await Task.Run(()=>engine.ApplyAsync("Direct · "+item.Title,plan.Select(p=>new Change(p.Id,p.Title,p.Before,p.Target)).ToList(),allowIndependent:true));item.Result=result.Success?"✓ Windows read-back verified":"× "+result.Message;item.ActionLabel=result.Success?"✓ Applied":"Retry";Status=item.Title+" · "+item.Result;if(result.Success)Actions.Remember(item.Title+" → "+Friendly(item.Id,target));await ReloadHistory();await RefreshSettings();}
  catch(Exception error){item.Result="× "+error.Message;item.ActionLabel="Retry";await ReloadHistory();await RefreshSettings();throw;}
  finally{item.ResetAction();Busy=false;}
 }
}
