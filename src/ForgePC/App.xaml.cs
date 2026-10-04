using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace ForgePC;
public partial class App : Application
{
 private Mutex? instance;
 protected override async void OnStartup(StartupEventArgs e)
 {
  base.OnStartup(e);
  if(e.Args.Length==2&&e.Args[0]=="--export-audit"){await File.WriteAllTextAsync(e.Args[1],System.Text.Json.JsonSerializer.Serialize(OptimizationAudit.All,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));Shutdown(0);return;}
  if(e.Args.Length==2&&e.Args[0]=="--maintenance-check"){try{var records=new List<object>();foreach(var id in new[]{"startup-info","delivery-scan"}){try{var result=await Maintenance.Run(id,"");using var doc=System.Text.Json.JsonDocument.Parse(result);records.Add(new{Action=id,ReadOnly=true,JsonCharacters=result.Length});}catch(Exception error){records.Add(new{Action=id,ReadOnly=true,Unsupported=error.Message});}}var bin=Maintenance.ReadBin();records.Add(new{Action="Recycle Bin read-only",Bytes=bin.Bytes,Items=bin.Items});records.Add(new{Action="DXGI inventory",Adapters=GraphicsInventory.Read()});await File.WriteAllTextAsync(e.Args[1],System.Text.Json.JsonSerializer.Serialize(records,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));Shutdown(0);}catch(Exception error){await File.WriteAllTextAsync(e.Args[1],error.ToString());Shutdown(1);}return;}
  if(e.Args.Length==2&&e.Args[0]=="--hardware-check"){try{var snapshot=await Task.Run(HardwareBackend.Inspect);await File.WriteAllTextAsync(e.Args[1],System.Text.Json.JsonSerializer.Serialize(snapshot,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));Shutdown(0);}catch(Exception error){await File.WriteAllTextAsync(e.Args[1],error.ToString());Shutdown(1);}return;}
  if(e.Args.Length==4&&e.Args[0]=="--maintenance"){try{Shutdown(await SystemCommands.RunMaintenanceHelper(e.Args[1],e.Args[2],e.Args[3]));}catch{Shutdown(1);}return;}
  if(e.Args.Length==5&&e.Args[0]=="--hardware-write"){try{Shutdown(await SystemCommands.RunHardwareHelper(e.Args[1],e.Args[2],e.Args[3],e.Args[4]));}catch{Shutdown(1);}return;}
  if(e.Args.Length==4&&e.Args[0]=="--cache-task"){try{Shutdown(await SystemCommands.RunCacheHelper(e.Args[1],e.Args[2],e.Args[3]));}catch{Shutdown(1);}return;}
  if(e.Args.Length==5&&e.Args[0]=="--service-write"){try{Shutdown(await SystemCommands.RunServiceHelper(e.Args[1],e.Args[2],e.Args[3],e.Args[4]));}catch{Shutdown(1);}return;}
  if(e.Args.Length==3&&e.Args[0]=="--system-task"){try{Shutdown(await SystemCommands.RunHelper(e.Args[1],e.Args[2]));}catch{Shutdown(1);}return;}
  if(e.Args.Length==1 && e.Args[0]=="--create-restore-point"){Shutdown(RestorePointService.RunDedicatedHelper());return;}
  if(e.Args.Length==2&&e.Args[0]=="--feature-smoke"){try{var results=new List<object>();foreach(var page in new[]{"CPU","GPU","Memory","Storage","Network","Diagnostics","Startup"}){var information=await Task.Run(()=>FeatureTools.Inspect(page));if(string.IsNullOrWhiteSpace(information))throw new IOException("Empty feature inventory.");results.Add(new{Section=page,ReadOnly=true,Characters=information.Length});}var services=await Task.Run(()=>WindowsSystemProbe.WmiBounded(@"root\cimv2","SELECT Name,State,StartMode FROM Win32_Service",2048,"Name","State","StartMode"));await File.WriteAllTextAsync(e.Args[1],System.Text.Json.JsonSerializer.Serialize(new{Sections=results,Services=services.Count,ReadOnly=true}));Shutdown(0);}catch{Shutdown(1);}return;}
  if(e.Args.Length==2&&e.Args[0]=="--gpu-probe"){using var probe=new GpuActivityProbe();var first=probe.Read();await Task.Delay(1200);await File.WriteAllTextAsync(e.Args[1],System.Text.Json.JsonSerializer.Serialize(new{Baseline=first,Sample=probe.Read(),ReadOnly=true}));Shutdown(0);return;}
  var performance=e.Args.Contains("--measure-performance");var validation=performance||e.Args.Contains("--adaptive-refresh-check")||e.Args.Contains("--v2-action-check")||e.Args.Contains("--smoke-test")||e.Args.Contains("--render-preview")||e.Args.Contains("--ui-check");
  var startup=System.Diagnostics.Stopwatch.StartNew();
  var dataRoot=validation?Path.Combine(Path.GetTempPath(),"EZoptimizer-validation-"+Guid.NewGuid().ToString("N")):Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"EZoptimizer");
  if(!validation){instance=new Mutex(true,@"Global\EZoptimizer-"+WindowsIdentity.GetCurrent().User?.Value,out var created);if(!created){MessageBox.Show("EZoptimizer is already running for this user.","EZoptimizer");Shutdown();return;}}
  try
  {
   var window=new MainWindow(dataRoot,e.Args.Contains("--recovery"));MainWindow=window;
   if(!validation||performance)window.Show();
   await window.InitializeAsync(!validation||performance);
   if(validation)
   {
    if(performance)
    {
     var readyMs=startup.ElapsedMilliseconds;var phases=new List<object>();
     foreach(var minimized in new[]{false,true})
     {
      window.WindowState=minimized?WindowState.Minimized:WindowState.Normal;
      await Task.Delay(10000); // Allow initial font/render work and polling interval changes to settle.
      using var process=System.Diagnostics.Process.GetCurrentProcess();process.Refresh();var cpuStart=process.TotalProcessorTime;var clock=System.Diagnostics.Stopwatch.StartNew();var memory=new List<long>();
      for(int i=0;i<20;i++){await Task.Delay(1000);process.Refresh();memory.Add(process.WorkingSet64);}
      process.Refresh();phases.Add(new{State=minimized?"Minimized":"Visible idle",Seconds=clock.Elapsed.TotalSeconds,CpuPercentOfMachine=(process.TotalProcessorTime-cpuStart).TotalSeconds/clock.Elapsed.TotalSeconds/Environment.ProcessorCount*100,AverageWorkingSetMiB=memory.Average()/1048576d,PeakWorkingSetMiB=memory.Max()/1048576d});
     }
     await File.WriteAllTextAsync(e.Args.Last(),System.Text.Json.JsonSerializer.Serialize(new{StartupToInventoryReadyMs=readyMs,LogicalProcessors=Environment.ProcessorCount,RenderingTier=RenderCapability.Tier>>16,WarmupSecondsPerPhase=10,Phases=phases,Limitations="One local run; warm disk cache, background apps uncontrolled. No tuning applied. Not a gaming-performance benchmark."},new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));Shutdown(0);return;
    }
    await Task.Delay(300);await window.ViewModel.TickAsync();
    if(window.ViewModel.Memory=="Unavailable")throw new IOException("Memory probe unavailable.");
    window.ViewModel.Theme=Arg(e.Args,"--theme")??"Dark";window.ViewModel.Page=Arg(e.Args,"--page")??"Overview";
    if(e.Args.Contains("--adaptive-refresh-check"))await V2Validation.CheckRefresh(window,e.Args.Last());
    if(e.Args.Contains("--v2-action-check"))await V2Validation.Execute(window,e.Args.Last());
    if(e.Args.Contains("--smoke-test")){window.ViewModel.RefreshDiagnostics.Execute(null);await WaitCommand(window.ViewModel.RefreshDiagnostics);if(window.ViewModel.Status.Contains("could not finish"))throw new IOException("Read-only inventory failed.");window.ViewModel.CheckReadiness.Execute(null);await WaitCommand(window.ViewModel.CheckReadiness);if(window.ViewModel.Readiness.Contains("Dictionary")||!window.ViewModel.Readiness.Contains("Power:"))throw new IOException("Readiness output failed.");window.ViewModel.StageProfile.Execute(null);await WaitCommand(window.ViewModel.StageProfile);window.ViewModel.Preview.Execute(null);await WaitCommand(window.ViewModel.Preview);if(window.ViewModel.Status.Contains("could not finish"))throw new IOException("Read-only preview failed.");}
    if(e.Args.Contains("--ui-check"))
    {
     var sizes=new[]{(Width:850,Height:600),(Width:1050,Height:680),(Width:1280,Height:800)};
     foreach(var theme in window.ViewModel.Themes)foreach(var page in window.ViewModel.Pages)foreach(var size in sizes)foreach(var scale in new[]{1d,1.5,2d}){window.ViewModel.Theme=theme;window.ViewModel.Page=page;var surface=Layout(window,size.Width,size.Height);window.VerifyPageLayout();var bitmap=new RenderTargetBitmap((int)(size.Width*scale),(int)(size.Height*scale),96*scale,96*scale,PixelFormats.Pbgra32);bitmap.Render(surface);}
     foreach(var page in window.ViewModel.Pages){window.ViewModel.Page=page;Layout(window,850,600);window.VerifyExpandedActions();}
     var report=new {ExpandedPages=window.ViewModel.Pages.Length,Pages=window.ViewModel.Pages.Length,Themes=4,Scales=new[]{100,150,200},Sizes=new[]{"850x600","1050x680","1280x800"},Layouts=window.ViewModel.Pages.Length*36,ReadOnly=true,MinimumDip=new[]{850,600},Sample=window.ViewModel.Stamp};await File.WriteAllTextAsync(e.Args.Last(),System.Text.Json.JsonSerializer.Serialize(report,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
    }
    if(e.Args.Contains("--render-preview"))
    {
     if(window.ViewModel.Page=="Overview")await window.ViewModel.SampleGraphicsForValidationAsync();
     var scale=double.TryParse(Arg(e.Args,"--scale"),System.Globalization.CultureInfo.InvariantCulture,out var s)?s:1;
     var width=int.TryParse(Arg(e.Args,"--width"),out var w)?w:1180;var height=int.TryParse(Arg(e.Args,"--height"),out var h)?h:800;
     var surface=Layout(window,width/scale,height/scale);var bitmap=new RenderTargetBitmap(width,height,96*scale,96*scale,PixelFormats.Pbgra32);bitmap.Render(surface);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var output=File.Create(e.Args.Last());encoder.Save(output);
    }
    window.ViewModel.Dispose();Shutdown(0);
   }
  }
  catch(Exception error){if(validation){await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(),"EZoptimizer-validation-error.txt"),error.ToString());Shutdown(1);}else{MessageBox.Show("EZoptimizer could not open. Your recovery records remain in local app data. Try --recovery. Error: "+error.GetType().Name,"EZoptimizer");Shutdown(1);}}
 }
 private static FrameworkElement Layout(MainWindow window,double width,double height){var surface=(FrameworkElement)window.Content;surface.Measure(new Size(width,height));surface.Arrange(new Rect(0,0,width,height));surface.UpdateLayout();return surface;}
 private static string? Arg(string[] args,string key){var index=Array.IndexOf(args,key);return index>=0&&index+1<args.Length?args[index+1]:null;}
 private static async Task WaitCommand(Command command){for(int i=0;i<200&&!command.CanExecute(null);i++)await Task.Delay(50);}
 protected override void OnExit(ExitEventArgs e){instance?.Dispose();base.OnExit(e);}
}
