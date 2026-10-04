using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ForgePC;

// Explicit developer-only integration mode. Normal smoke/layout validation never mutates Windows.
internal static class V2Validation
{
 private static IEnumerable<FrameworkElement> Elements(DependencyObject root)
 {for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is FrameworkElement element)yield return element;foreach(var nested in Elements(child))yield return nested;}}
 private static async Task Run(Command command,object? parameter=null)
 {if(!command.CanExecute(parameter))throw new IOException("Validation command is disabled.");command.Execute(parameter);for(int i=0;i<600&&!command.CanExecute(parameter);i++)await Task.Delay(100);if(!command.CanExecute(parameter))throw new TimeoutException("Validation action did not finish.");}
 public static async Task CheckRefresh(MainWindow window,string output)
 {
  var results=new List<object>();foreach(var page in new[]{"CPU","GPU","Memory","Network","Storage"}){window.ViewModel.Page=page;await Run(window.ViewModel.Tools.Refresh);var info=window.ViewModel.Tools.Information;if(!info.Contains("Recommended for this PC")||info.Contains("Hardware scan failed:"))throw new IOException(page+" refresh did not rebuild hardware recommendations.");results.Add(new{Page=page,Passed=true,VisibleControls=window.ViewModel.PageOperations.Count(),Summary=window.ViewModel.RecommendationSummary});}
  await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{ReadOnly=true,Results=results},new JsonSerializerOptions{WriteIndented=true}));
 }
 public static async Task Execute(MainWindow window,string output)
 {
  var vm=window.ViewModel;var backend=new WindowsSettings();var original=backend.Read("menus");var target=original=="On"?"Off":"On";var results=new List<object>();
  try
  {
   vm.Page="Optimize";vm.Category="Windows";var surface=(FrameworkElement)window.Content;surface.Measure(new Size(1050,680));surface.Arrange(new Rect(0,0,1050,680));surface.UpdateLayout();
   var item=vm.Operations.Single(o=>o.Id=="menus");item.Target=target;
   var button=Elements(surface).OfType<Button>().Single(b=>b.Command==vm.ApplyOne&&b.CommandParameter==item);
   await Run((Command)button.Command,button.CommandParameter);
   if(backend.Read("menus")!=target||!item.Result.StartsWith("✓"))throw new IOException("Bound Apply button did not produce a verified Windows change.");
   results.Add(new{Action="Bound menu animation Apply command",Passed=true,Backend="SystemParametersInfo",ReadBack=target});
   if(!item.CanRestore)throw new IOException("Row restore action was not enabled.");await Run(vm.RestoreOne,item);
   if(backend.Read("menus")!=original)throw new IOException("Native original was not restored.");results.Add(new{Action="Recovery command",Passed=true,ReadBack=original});
   var numeric=vm.Operations.Single(o=>o.Id=="menu-delay");var beforeNumeric=backend.Read(numeric.Id);numeric.Target="invalid";await Run(vm.ApplyOne,numeric);if(!numeric.Result.StartsWith("×")||backend.Read(numeric.Id)!=beforeNumeric)throw new IOException("Failure feedback or no-write validation failed.");numeric.Target=beforeNumeric;
   results.Add(new{Action="Invalid target failure feedback",Passed=true});
   await Run(vm.ApplyOne,item);if(backend.Read("menus")!=target)throw new IOException("Toggle did not apply.");await Run(vm.ApplyOne,item);if(backend.Read("menus")!=original||item.CanRestore)throw new IOException("Opposite button did not restore the first original.");results.Add(new{Action="Binary opposite action restores original",Passed=true});
   await Run(vm.Actions.FlushDns);if(vm.Actions.DnsLabel!="✓ Flushed")throw new IOException("DNS command failed: "+vm.Actions.Status);
   results.Add(new{Action="DNS command",Passed=true,Output=vm.Actions.Details});
   string response="";var code=await SystemCommands.ReadHelper(["--cache-task","prefetch","scan"],text=>response=text);var scan=JsonSerializer.Deserialize<CacheOutcome>(response);
   if(code!=0||scan?.Id!="prefetch")throw new IOException("Protected read-only helper scan failed.");
   results.Add(new{Action="Elevated helper transport / Prefetch scan only",Passed=true,Result=scan});
   await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{Results=results,HostWasAdministrator=SystemCommands.IsAdministrator,Limitations="Only menu animation apply/restore, failure feedback, DNS flush and protected scan exercised on this host. No cache of the host was deleted. No Explorer restart, service writes, power writes, real UAC consent/cancel or Windows 11 certification."},new JsonSerializerOptions{WriteIndented=true}));
  }
  finally
  {
   // Retry through durable recovery if a validation assertion failed. Never overwrite an external change.
   foreach(var active in vm.History.Where(j=>j.IsActive).ToArray())await Run(vm.Undo,active);
   if(backend.Read("menus")!=original)throw new IOException("Integration check left menu recovery unresolved; inspect its temporary history.");
  }
 }
}

