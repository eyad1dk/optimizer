using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
namespace ForgePC;

public enum Evidence { DocumentedBehavior, WorkloadDependent, Guidance }
public record OperationDefinition(string Id, int Version, string Title, string Category, string Purpose,
 string Tradeoff, string Documentation, Evidence Evidence = Evidence.DocumentedBehavior,
 string Risk = "Low · user preference", string Recovery = "Exact saved value", bool Restart = false,
 bool RequiresAdministrator = false, bool RequiresRestorePoint = false, string[]? Dependencies = null);
public static partial class Catalog
{
 public static readonly OperationDefinition[] Operations = [
  new("animations", 1, "App animations", "Windows", "Control transient motion inside Windows apps.",
   "Reduced motion can feel more immediate. Apps may ignore this preference; no FPS improvement is implied.",
   "https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfow"),
  new("menus", 1, "Menu animations", "Windows", "Show native Windows menus with or without animation.",
   "Turning this off changes appearance. It does not measure or reduce game input latency.",
   "https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfow"),
  new("power", 1, "Installed power plan", "Power", "Choose an existing Windows power plan.",
   "Higher performance plans can increase heat, noise and battery use. OEM and managed restrictions remain authoritative.",
   "https://learn.microsoft.com/en-us/windows-hardware/design/device-experiences/powercfg-command-line-options",
   Evidence.WorkloadDependent, "Moderate · energy and heat"),
  .. NativePreferences.Additional,
  PointerAcceleration.Definition,
  .. ProcessorPower.Definitions,
  .. ServicePreferences.Definitions
 ];
 public static OperationDefinition Get(string id) => Operations.SingleOrDefault(o => o.Id == id)
  ?? (HardwareBackend.Contains(id)?HardwareBackend.Definition(id):throw new InvalidDataException("Unknown operation ID."));
 public static void ValidateTarget(string id, string value)
 {
  Get(id);
  if(HardwareBackend.Contains(id)){HardwareBackend.Validate(id,value);return;}
  if (id == "power") { if (!Guid.TryParseExact(value, "D", out var guid) || guid.ToString("D")!=value) throw new InvalidDataException("Invalid power plan."); }
  else if(id==PointerAcceleration.Id) PointerAcceleration.Validate(value);
  else if(ServicePreferences.Contains(id)) ServicePreferences.Parse(value);
  else if (ProcessorPower.Ids.Contains(id)) ProcessorPower.Validate(id,value);
  else if (NativePreferences.Get(id).Numeric) NativePreferences.ParseNumber(NativePreferences.Get(id),value);
  else if (value is not ("On" or "Off")) throw new InvalidDataException("Expected On or Off.");
 }
 public static string ValueType(string id) => HardwareBackend.Contains(id)?"HardwareConfiguration": id == "power" ? "Guid" : id==PointerAcceleration.Id?"MouseParameters":ServicePreferences.Contains(id)?"ServiceConfiguration":ProcessorPower.Ids.Contains(id) ? (id.Contains("-boost-")?"SchemeIndex":"SchemePercentage") : NativePreferences.Get(id).Numeric ? "Integer" : "Boolean";
}
public record Capability(bool Eligible, string Reason);
public interface ICapabilityService { Capability Check(string id, string target); Capability CheckRestore(string id,string original)=>Check(id,original); }
public sealed class PermissiveCapabilities : ICapabilityService
{
 public Capability Check(string id, string target) { Catalog.ValidateTarget(id, target); return new(true, "Eligible"); }
}
public record PlannedChange(string Id, int Version, string Title, string Before, string Target, string ValueType);
public interface IExecutionPlanner { IReadOnlyList<PlannedChange> Preview(IEnumerable<KeyValuePair<string,string>> targets); }
public sealed class ExecutionPlanner(ISettings settings, ICapabilityService capabilities) : IExecutionPlanner
{
 public IReadOnlyList<PlannedChange> Preview(IEnumerable<KeyValuePair<string,string>> targets)
 {
  var seen = new HashSet<string>(); var result = new List<PlannedChange>();
  foreach (var (id, target) in targets)
  {
   if (!seen.Add(id)) throw new InvalidDataException("Duplicate operation.");
   var definition = Catalog.Get(id); var capability = capabilities.Check(id, target);
   if (!capability.Eligible) throw new InvalidOperationException(definition.Title + ": " + capability.Reason);
   if (definition.RequiresRestorePoint)
    throw new InvalidOperationException("System-level operations are not enabled in this catalog.");
   var before = settings.Read(id);
   if (before != target) result.Add(new(id, definition.Version, definition.Title, before, target, Catalog.ValueType(id)));
  }
  if(result.Any(x=>x.Id=="power")&&result.Any(x=>ProcessorPower.Ids.Contains(x.Id)))throw new InvalidOperationException("Apply a power-plan switch separately from processor settings, then preview again.");
  foreach (var entry in result)
   foreach (var dependency in Catalog.Get(entry.Id).Dependencies ?? [])
    if (!seen.Contains(dependency)) throw new InvalidOperationException("A required dependency is missing.");
  var ordered=OperationOrdering.Order(result.Select(p=>new Change(p.Id,p.Title,p.Before,p.Target)));return ordered.Select(c=>result.Single(p=>p.Id==c.Key)).ToArray();
 }
}
public abstract class Observable : INotifyPropertyChanged
{
 public event PropertyChangedEventHandler? PropertyChanged;
 protected bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
 { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Raise(property); return true; }
 protected internal void Raise([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
}
public sealed class OperationItem(OperationDefinition definition) : Observable
{
 public System.Collections.ObjectModel.ObservableCollection<ValueOption> Choices {get;}=[];
 public OperationDefinition Definition { get; } = definition;
 public string Id => Definition.Id;
 public string Title => Definition.Title;
 public HardwareControl? Hardware {get;set;}
 public string Raw {get;private set;}="";
 public bool IsBinary=>Hardware?.Binary??(!HardwareBackend.Contains(Id)&&Id!="power"&&Id!=PointerAcceleration.Id&&!ServicePreferences.Contains(Id)&&!ProcessorPower.Ids.Contains(Id)&&!NativePreferences.Get(Id).Numeric);
 public bool HasValueSelector=>!IsBinary;
 public string BinaryTarget(string value)=>Hardware is {} h?(h.Effective(value)==h.On?h.Off:h.On):(value=="On"?"Off":"On");
 public void Detect(string value){Raw=value;Raise(nameof(Raw));Raise(nameof(IsBinary));Raise(nameof(HasValueSelector));ResetAction();}
 public void ResetAction()=>ActionLabel=Id.StartsWith("hw:service:")?(Raw=="On"?"Stop":"Start"):IsBinary?(BinaryTarget(Raw)==(Hardware?.On??"On")?"Enable":"Disable"):"Apply";
 private string badge="",reason="";
 public string Badge{get=>badge;set=>Set(ref badge,value);}
 public string RecommendationReason{get=>reason;set{Set(ref reason,value);Raise(nameof(AuditDetails));}}
 private string actionLabel="Apply",result="";
 public string ActionLabel{get=>actionLabel;set=>Set(ref actionLabel,value);}
 public string Result{get=>result;set=>Set(ref result,value);}
 public string Shield=>Definition.RequiresAdministrator?"🛡":"";
 public string Meta => Definition.Category + " · " + Definition.Risk + (Definition.Restart?" · Restart may be required":" · No restart") + (Definition.RequiresAdministrator?" · Administrator":"");
 private string current = "Not read", target = "", compatibility = "Checking";
 private bool eligible,canRestore;
 public bool CanRestore{get=>canRestore;set{Set(ref canRestore,value);Raise(nameof(AuditDetails));}}
 private string original="No saved original";
 public string Original{get=>original;set{Set(ref original,value);Raise(nameof(AuditDetails));}}
 public string Current { get => current; set {Set(ref current, value);Raise(nameof(AuditDetails));} }
 public string Target { get => target; set { if(value!=null)Set(ref target,value); } }
 public string Compatibility { get => compatibility; set => Set(ref compatibility, value); }
 public bool Eligible { get => eligible; set => Set(ref eligible, value); }
 public string Purpose => Definition.Purpose;
 public string Tradeoff => Definition.Tradeoff;
 public string Recovery => Definition.Recovery;
 public string AuditDetails=>OptimizationAudit.For(Definition).Summary+"\nWhy recommended: "+(RecommendationReason.Length>0?RecommendationReason:"No automatic recommendation: this is a manual preference or no matching supported hardware/workload rule was found.")+"\nOriginal value: "+Original+"\nCurrent value: "+Current+"\nRestore: "+(CanRestore?"Saved original available":"No active saved change")+(Id.StartsWith("service:")||Id.StartsWith("hw:service:")?"\nService classification: CAUTION — optional feature may stop working. Critical services are excluded from all mutation controls.":"");
 public string Evidence => Definition.Evidence.ToString();
 public string SupportedWindows=>"Windows 10 22H2 / Windows 11 · capability checked";
 public string Level=>Definition.Risk.StartsWith("Advanced")||Definition.Risk.StartsWith("Moderate")?"ADVANCED":"SAFE";
}
public record ValueOption(string Value,string Label){public override string ToString()=>Label;}
public record Guidance(string Title, string Category, string Description, string Action, string Url, string Evidence)
{
 public string Type => "Windows shortcut · no automatic change";
}
public static class GuidanceCatalog
{
 public static readonly Guidance[] Items = [
  new("Game Mode", "Gaming", "Review Windows Game Mode for your games. Its effect depends on the workload.", "Open Game Mode", "ms-settings:gaming-gamemode", "Windows supported settings"),
  new("Background recording", "Gaming", "Review captures if you record clips. Turning recording off removes that functionality.", "Open captures", "ms-settings:gaming-gamedvr", "Windows supported settings"),
  new("Per-app graphics", "Gaming", "Choose graphics preferences for hybrid GPUs in Windows. Keep OEM controls intact.", "Open graphics", "ms-settings:display-advancedgraphics", "Windows supported settings"),
  new("Display and refresh rate", "Gaming", "Check the selected refresh rate. Availability depends on the monitor, cable and GPU.", "Open display", "ms-settings:display-advanced", "Windows supported settings"),
  new("Advertising and personalization", "Privacy", "Review advertising ID and personalized suggestions. Existing managed policies take precedence.", "Review privacy", "ms-settings:privacy-general", "Windows supported settings"),
  new("App permissions", "Privacy", "Review camera and microphone access per app. Revoking access can break calls or recording.", "Review permissions", "ms-settings:privacy-webcam", "Windows supported settings"),
  new("Installed applications", "Windows", "Inventory is for review. Names alone do not show that an app is unnecessary. Uninstalling can remove user data.", "Open installed apps", "ms-settings:appsfeatures", "Windows uninstall workflow"),
  new("Startup review", "Startup", "Review what starts at sign-in. The local inventory is partial; Windows also lists packaged startup apps.", "Manage startup", "ms-settings:startupapps", "Windows supported settings"),
  new("Storage cleanup", "Cleanup", "Review Windows-managed cleanup. Downloads and personal files are not selected by this app.", "Open storage", "ms-settings:storagesense", "Windows-managed cleanup"),
  new("Network settings", "Network", "Inspect adapters and DNS in Diagnostics before troubleshooting. DNS edits use saved originals; broad Windows Network Reset is outside app recovery.", "Open network", "ms-settings:network-status", "Windows supported settings"),
  new("Notifications", "Windows", "Reduce interruptions for coding or calls using Windows controls.", "Open notifications", "ms-settings:notifications", "Windows supported settings"),
  new("Windows updates", "Windows", "Keep security servicing current. EZoptimizer never disables updates.", "Open updates", "ms-settings:windowsupdate", "Windows supported settings")
 ];
}
