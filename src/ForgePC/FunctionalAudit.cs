using System.IO;
using System.Text.Json;
namespace ForgePC;

public static class FunctionalAudit
{
 public static void Export(string path)
 {
  var settings=new WindowsSettings();var capabilities=new WindowsCapabilityService(settings);
  var native=Catalog.Operations.Select(d=>{try{var value=settings.Read(d.Id);return new{d.Id,Supported=capabilities.Check(d.Id,value).Eligible,Contract=OptimizationAudit.For(d)};}catch{return new{d.Id,Supported=false,Contract=OptimizationAudit.For(d)};}}).ToArray();
  var hardware=HardwareBackend.Inspect();
  var adaptive=hardware.Controls.Select(c=>new{Id=c.Id,Supported=c.Supported&&c.Id!="hw:pagefile",Contract=OptimizationAudit.For(HardwareBackend.Definition(c.Id))}).ToArray();
  var actions=Catalog.Actions.Select(a=>new{a.Id,a.Category,a.Method,a.Administrator,a.Verification,a.Recovery,Support=a.Supported()}).ToArray();
  File.WriteAllText(path,JsonSerializer.Serialize(new{GeneratedUtc=DateTime.UtcNow,OSBuild=Environment.OSVersion.Version.Build,NativeDefined=native.Length,NativeDetected=native.Count(x=>x.Supported),AdaptiveDetected=adaptive.Count(x=>x.Supported),AdditionalReversibleTools=new[]{"Current-user startup registrations","Selected process priority"},RegisteredActionTools=actions.Length,PlatformEligibleActionTools=actions.Count(a=>a.Support.Eligible),RecommendationRules=RecommendationEngine.RuleIds,Native=native,Adaptive=adaptive,Actions=actions,Limitations="Detected support is not a claim of native write certification. Action cmdlets/device/filesystem support are checked when invoked. Private original values and inventory are excluded."},new JsonSerializerOptions{WriteIndented=true}));
 }
}
