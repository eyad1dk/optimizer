using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
namespace ForgePC;
public sealed class WindowsSettings : IConditionalSettings, IChangeGuardedSettings
{
 [DllImport("user32.dll", EntryPoint="SystemParametersInfoW", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
 private static extern bool GetParameter(uint action,uint parameter,out int value,uint flags);
 [DllImport("user32.dll", EntryPoint="SystemParametersInfoW", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
 private static extern bool SetParameter(uint action,uint parameter,IntPtr value,uint flags);
 [StructLayout(LayoutKind.Sequential)] private struct AnimationInfo { public uint Size; public int Enabled; }
 [DllImport("user32.dll", EntryPoint="SystemParametersInfoW", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
 private static extern bool AnimationParameter(uint action,uint parameter,ref AnimationInfo value,uint flags);
 public Action? PrepareConnectivityCheck(string id)=>NetworkGuard.Prepare(id);
 public string Read(string key)
 {
  if(HardwareBackend.Contains(key))return HardwareBackend.Read(key).Value;
  if(key==PointerAcceleration.Id)return PointerAcceleration.Read();
  if(ServicePreferences.Contains(key))return ServicePreferences.Read(key);
  if(ProcessorPower.Ids.Contains(key))return ProcessorPower.Read(key);
  if (key == "power") return GuidFrom(RunPower("/getactivescheme"));
  var preference=NativePreferences.Get(key); int v;
  if(preference.Parameter==NativeParameter.AnimationStructure)
  {
   var info=new AnimationInfo{Size=(uint)Marshal.SizeOf<AnimationInfo>()};
   if(!AnimationParameter(preference.Get,info.Size,ref info,0))throw new Win32Exception(Marshal.GetLastWin32Error());
   v=info.Enabled;
  }
  else if(!GetParameter(preference.Get,0,out v,0))throw new Win32Exception(Marshal.GetLastWin32Error());
  var result=preference.Numeric?v.ToString(System.Globalization.CultureInfo.InvariantCulture):v==0?"Off":"On";
  Catalog.ValidateTarget(key,result);return result;
 }
 public void WriteIfUnchanged(string key,string expected,string value)
 {if(HardwareBackend.Contains(key)){HardwareBackend.Write(key,expected,value);return;}if(Read(key)!=expected)throw new InvalidOperationException("The preview is stale. Refresh and review again.");if(ServicePreferences.Contains(key)){if(SystemCommands.IsAdministrator)ServicePreferences.Write(key,expected,value);else SystemCommands.WriteServiceAsync(key,expected,value).GetAwaiter().GetResult();return;}Write(key,value);}
 public void Write(string key,string value)
 {
  Catalog.ValidateTarget(key,value);
  if(HardwareBackend.Contains(key)){HardwareBackend.Write(key,Read(key),value);return;}
  if(key==PointerAcceleration.Id){PointerAcceleration.Write(value);return;}
  if(ServicePreferences.Contains(key)){WriteIfUnchanged(key,Read(key),value);return;}
  if(ProcessorPower.Ids.Contains(key)){ProcessorPower.Write(key,value);return;}
  if (key == "power")
  {
   if (!Plans().Any(p => p.Id == value)) throw new InvalidOperationException("This power plan is no longer installed.");
   RunPower("/setactive",value); return;
  }
  var preference=NativePreferences.Get(key); var number=preference.Numeric?NativePreferences.ParseNumber(preference,value):value=="On"?1:0;
  bool success;
  if(preference.Parameter==NativeParameter.AnimationStructure)
  {var info=new AnimationInfo{Size=(uint)Marshal.SizeOf<AnimationInfo>(),Enabled=number};success=AnimationParameter(preference.Set,info.Size,ref info,3);}
  else success=SetParameter(preference.Set,preference.Parameter==NativeParameter.UiValue?(uint)number:0,preference.Parameter==NativeParameter.PointerValue?new IntPtr(number):IntPtr.Zero,3);
  if(!success)throw new Win32Exception(Marshal.GetLastWin32Error());
 }
 public List<PowerPlan> Plans() => RunPower("/list").Split('\n').Where(l => Regex.IsMatch(l,"[a-fA-F0-9]{8}-[a-fA-F0-9-]{27}"))
  .Select(l => new PowerPlan(GuidFrom(l),Regex.Match(l,@"\((.*)\)").Groups[1].Value)).ToList();
 private static string GuidFrom(string value) => Guid.Parse(Regex.Match(value,"[a-fA-F0-9]{8}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{12}").Value).ToString();
 private static string RunPower(params string[] args)
 {
  var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"powercfg.exe"))
  { UseShellExecute=false, RedirectStandardOutput=true, RedirectStandardError=true, CreateNoWindow=true };
  foreach (var arg in args) start.ArgumentList.Add(arg);
  using var p = Process.Start(start) ?? throw new IOException("Windows power configuration is unavailable.");
  var output = p.StandardOutput.ReadToEndAsync(); var error = p.StandardError.ReadToEndAsync();
  if (!p.WaitForExit(8000)) { p.Kill(); throw new TimeoutException("Power configuration timed out."); }
  Task.WaitAll(output,error);
  if (p.ExitCode != 0) throw new IOException("Windows refused power configuration. Review policy and privileges in Windows.");
  return output.Result;
 }
}
