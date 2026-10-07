using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace ForgePC;

public record SystemTask(string Id,string Name,string Executable,string[] Arguments,bool Administrator,string Warning);
public record TaskMessage(string Kind,string Text,int? ExitCode=null);
public static class SystemCommands
{
 public static SystemTask[] Tasks=>Catalog.SystemTasks;
 public static SystemTask Get(string id)=>Tasks.SingleOrDefault(t=>t.Id==id)??throw new InvalidDataException("Unknown system action.");
 public static bool IsAdministrator{get{using var identity=WindowsIdentity.GetCurrent();return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);}}
 [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeClientProcessId(IntPtr pipe,out uint pid);
 public static async Task<int> RunAsync(string id,Action<string> output)
 {
  var task=Get(id);var support=Catalog.Action(id).Supported();if(!support.Eligible)throw new InvalidOperationException(support.Reason);
  if(!task.Administrator||IsAdministrator)return await RunLocal(task,m=>{output(m.Text);return Task.CompletedTask;});
  return await ReadHelper(["--system-task",id],output);
 }
 public static async Task WriteServiceAsync(string id,string expected,string target)
 {
  if(!ServicePreferences.Contains(id))throw new InvalidDataException("Unknown optional service.");ServicePreferences.Parse(expected);ServicePreferences.Parse(target);
  string result="";var code=await ReadHelper(["--service-write",id,expected,target],text=>result=text);if(code!=0)throw new InvalidOperationException("Windows could not verify the optional service change ("+result+"). Original configuration remains in recovery; review permissions and retry.");
 }
 public static async Task<int> RunServiceHelper(string id,string expected,string target,string pipeName)
 {
  if(!ServicePreferences.Contains(id)||!IsAdministrator||!ValidPipe(pipeName))return 2;
  ServicePreferences.Parse(expected);ServicePreferences.Parse(target);
  using var pipe=new NamedPipeClientStream(".",pipeName,PipeDirection.Out,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);await pipe.ConnectAsync(10000);using var writer=new StreamWriter(pipe){AutoFlush=true};
  try{ServicePreferences.Write(id,expected,target);if(ServicePreferences.Read(id)!=target)throw new IOException("Service read-back failed.");await writer.WriteLineAsync(JsonSerializer.Serialize(new TaskMessage("result","Service configuration verified",0)));return 0;}
  catch(Exception error){await writer.WriteLineAsync(JsonSerializer.Serialize(new TaskMessage("result",error is System.ComponentModel.Win32Exception native?"Windows error "+native.NativeErrorCode:error.GetType().Name,1)));return 1;}
 }
 public static async Task<int> RunMaintenanceHelper(string id,string argument,string pipeName)
 {
  if(!IsAdministrator||!ValidPipe(pipeName))return 2;argument=HardwareBackend.Decode(argument);Maintenance.Validate(id,argument);
  using var pipe=new NamedPipeClientStream(".",pipeName,PipeDirection.Out,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);await pipe.ConnectAsync(10000);using var writer=new StreamWriter(pipe){AutoFlush=true};
  try{var result=Maintenance.Local(id,argument);await writer.WriteLineAsync(JsonSerializer.Serialize(new TaskMessage("result",result,0)));return 0;}catch(Exception error){await writer.WriteLineAsync(JsonSerializer.Serialize(new TaskMessage("result",error.Message,1)));return 1;}
 }
 public static async Task<int> RunHardwareHelper(string id,string expected,string target,string pipeName)
 {
  if(!IsAdministrator||!ValidPipe(pipeName))return 2;HardwareBackend.ValidateId(id);expected=HardwareBackend.Decode(expected);target=HardwareBackend.Decode(target);
  using var pipe=new NamedPipeClientStream(".",pipeName,PipeDirection.Out,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);await pipe.ConnectAsync(10000);using var writer=new StreamWriter(pipe){AutoFlush=true};
  try{HardwareBackend.WriteLocal(id,expected,target);await writer.WriteLineAsync(JsonSerializer.Serialize(new TaskMessage("result","Windows configuration verified",0)));return 0;}
  catch(Exception error){await writer.WriteLineAsync(JsonSerializer.Serialize(new TaskMessage("result",error.Message,1)));return 1;}
 }
 private static bool ValidPipe(string name)=>name.StartsWith("EZoptimizer-task-",StringComparison.Ordinal)&&Guid.TryParseExact(name[17..],"N",out _);
 internal static async Task<int> ReadHelper(string[] arguments,Action<string> output)
 {
  string pipeName="EZoptimizer-task-"+Guid.NewGuid().ToString("N");
  using var pipe=new NamedPipeServerStream(pipeName,PipeDirection.In,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
  var start=new ProcessStartInfo(Environment.ProcessPath??throw new InvalidOperationException("Executable location unavailable.")){UseShellExecute=true,Verb="runas",WorkingDirectory=AppContext.BaseDirectory};
  // Framework-dependent developer runs use the same fixed assembly, never a supplied script.
  if(Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet",StringComparison.OrdinalIgnoreCase))start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory,"EZoptimizer.dll"));
  foreach(var argument in arguments)start.ArgumentList.Add(argument);start.ArgumentList.Add(pipeName);
  using var helper=Process.Start(start)??throw new IOException("Administrator helper did not start.");
  await pipe.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(60));
  if(!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(),out uint client)||client!=helper.Id)throw new IOException("Unexpected helper process.");
  using var reader=new StreamReader(pipe);int? exit=null;string? line;
  while((line=await reader.ReadLineAsync())!=null)
  {
   if(line.Length>32768)throw new InvalidDataException("Helper message exceeded its limit.");
   var message=JsonSerializer.Deserialize<TaskMessage>(line)??throw new InvalidDataException("Invalid helper response.");output(message.Text);if(message.Kind=="result")exit=message.ExitCode;
  }
  await helper.WaitForExitAsync();return exit??throw new IOException("Helper ended without a command result. Check Windows servicing logs before retrying.");
 }
 public static async Task<CacheOutcome> CleanupAsync(string id,bool clean)
 {
  var spec=CacheCatalog.Get(id);
  if(!spec.Administrator||IsAdministrator)return await Task.Run(()=>new CacheCleaner(spec).Execute(clean));
  string response="";var code=await ReadHelper(["--cache-task",id,clean?"clean":"scan"],text=>response=text);
  if(code!=0)throw new IOException("Cleanup helper failed: "+response);
  var result=JsonSerializer.Deserialize<CacheOutcome>(response)??throw new IOException("Missing cleanup result.");
  if(result.Id!=id)throw new IOException("Unexpected cleanup result.");return result;
 }
 public static async Task<int> RunCacheHelper(string id,string mode,string pipeName)
 {
  var spec=CacheCatalog.Get(id);if(!spec.Administrator||!IsAdministrator||!ValidPipe(pipeName)||mode is not("scan" or "clean"))return 2;
  using var pipe=new NamedPipeClientStream(".",pipeName,PipeDirection.Out,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);await pipe.ConnectAsync(10000);using var writer=new StreamWriter(pipe){AutoFlush=true};
  try{var result=new CacheCleaner(spec).Execute(mode=="clean");await writer.WriteLineAsync(JsonSerializer.Serialize(new TaskMessage("result",JsonSerializer.Serialize(result),0)));return 0;}
  catch(Exception error){await writer.WriteLineAsync(JsonSerializer.Serialize(new TaskMessage("result",error.GetType().Name+": "+error.Message,1)));return 1;}
 }
 public static async Task<int> RunHelper(string id,string pipeName)
 {
  Get(id);if(!IsAdministrator||!ValidPipe(pipeName))return 2;
  using var pipe=new NamedPipeClientStream(".",pipeName,PipeDirection.Out,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);await pipe.ConnectAsync(10000);
  using var writer=new StreamWriter(pipe){AutoFlush=true};
  try{return await RunLocal(Get(id),message=>writer.WriteLineAsync(JsonSerializer.Serialize(message)));}
  catch(Exception error){await writer.WriteLineAsync(JsonSerializer.Serialize(new TaskMessage("result","System action failed: "+error.GetType().Name,1)));return 1;}
 }
 [DllImport("kernel32.dll")] private static extern uint GetOEMCP();
 private static async Task<int> RunLocal(SystemTask task,Func<TaskMessage,Task> output)
 {
  var start=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,task.Executable)){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};foreach(var argument in task.Arguments)start.ArgumentList.Add(argument);
  System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);var encoding=task.Executable=="sfc.exe"?System.Text.Encoding.Unicode:System.Text.Encoding.GetEncoding((int)GetOEMCP());start.StandardOutputEncoding=encoding;start.StandardErrorEncoding=encoding;
  using var process=Process.Start(start)??throw new IOException("Windows tool could not start.");
  using var gate=new SemaphoreSlim(1);async Task Pump(StreamReader reader){var buffer=new char[2048];int count;while((count=await reader.ReadAsync(buffer))>0){await gate.WaitAsync();try{await output(new("output",new string(buffer,0,count)));}finally{gate.Release();}}}
  await Task.WhenAll(Pump(process.StandardOutput),Pump(process.StandardError),process.WaitForExitAsync());
  await output(new("result",$"\nWindows command finished with exit code {process.ExitCode}. Review its output for findings.\n",process.ExitCode));return process.ExitCode;
 }
}
