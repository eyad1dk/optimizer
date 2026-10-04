using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace ForgePC;

// Values include the scheme identity so a stale preview cannot modify a different plan.
public static class ProcessorPower
{
 public static readonly string[] Ids=["cpu-min-ac","cpu-max-ac","cpu-min-dc","cpu-max-dc","cpu-boost-ac","cpu-boost-dc"];
 private static Guid Group=new("54533251-82be-4824-96c1-47b60b740d00");
 private static Guid Minimum=new("893dee8e-2bef-41e0-89c6-b55d0929964c");
 private static Guid Boost=new("be337238-0d82-4146-a960-4f3749d470c7");
 private static Guid Maximum=new("bc5038f7-23e0-4960-96da-33abaf5935ec");
 public const string Documentation="https://learn.microsoft.com/en-us/windows-hardware/customize/power-settings/options-for-perf-state-engine-maxperformance";
 [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr root,out IntPtr scheme);
 [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
 [DllImport("powrprof.dll")] private static extern uint PowerReadACValueIndex(IntPtr root,ref Guid scheme,ref Guid group,ref Guid setting,out uint value);
 [DllImport("powrprof.dll")] private static extern uint PowerReadDCValueIndex(IntPtr root,ref Guid scheme,ref Guid group,ref Guid setting,out uint value);
 [DllImport("powrprof.dll")] private static extern uint PowerWriteACValueIndex(IntPtr root,ref Guid scheme,ref Guid group,ref Guid setting,uint value);
 [DllImport("powrprof.dll")] private static extern uint PowerWriteDCValueIndex(IntPtr root,ref Guid scheme,ref Guid group,ref Guid setting,uint value);
 [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr root,ref Guid scheme);
 public static Guid SettingId(string id){if(!Ids.Contains(id))throw new InvalidDataException("Unknown processor setting.");return id.Contains("-boost-")?Boost:id.Contains("-min-")?Minimum:Maximum;}
 public static Guid ActiveScheme(){Check(PowerGetActiveScheme(IntPtr.Zero,out var pointer));try{return Marshal.PtrToStructure<Guid>(pointer);}finally{LocalFree(pointer);}}
 private static void Check(uint result){if(result!=0)throw new Win32Exception((int)result);}
 public static (Guid Scheme,uint Percent) Parse(string value)
 {
  var parts=value.Split('|');if(parts.Length!=2||!Guid.TryParseExact(parts[0],"D",out var scheme)||scheme.ToString("D")!=parts[0]||!uint.TryParse(parts[1],NumberStyles.None,CultureInfo.InvariantCulture,out var number)||number>100||number.ToString(CultureInfo.InvariantCulture)!=parts[1])throw new InvalidDataException("Expected a power scheme and a processor percentage from 0 to 100.");return(scheme,number);
 }
 public static string Read(string id)
 {
  if(!Ids.Contains(id))throw new InvalidDataException("Unknown processor setting.");var scheme=ActiveScheme();var setting=SettingId(id);uint value;
  Check(id.EndsWith("-ac")?PowerReadACValueIndex(IntPtr.Zero,ref scheme,ref Group,ref setting,out value):PowerReadDCValueIndex(IntPtr.Zero,ref scheme,ref Group,ref setting,out value));
  var result=$"{scheme:D}|{value.ToString(CultureInfo.InvariantCulture)}";Validate(id,result);return result;
 }
 public static void Write(string id,string value)
 {
  if(!Ids.Contains(id))throw new InvalidDataException("Unknown processor setting.");Validate(id,value);var(scheme,number)=Parse(value);if(ActiveScheme()!=scheme)throw new InvalidOperationException("Active power plan changed. Select the original plan before retrying.");
  if(!id.Contains("-boost-")){var counterpart=Parse(Read(id.Replace(id.Contains("-min-")?"-min-":"-max-",id.Contains("-min-")?"-max-":"-min-"))).Percent;
  if(id.Contains("-min-")?number>counterpart:number<counterpart)throw new InvalidOperationException("Minimum processor state cannot exceed maximum. Adjust the other boundary first.");}
  var setting=SettingId(id);
  Check(id.EndsWith("-ac")?PowerWriteACValueIndex(IntPtr.Zero,ref scheme,ref Group,ref setting,number):PowerWriteDCValueIndex(IntPtr.Zero,ref scheme,ref Group,ref setting,number));
  // Reactivation is required by the documented API before the new index takes effect.
  if(ActiveScheme()!=scheme)throw new InvalidOperationException("The power plan changed during the write. Recovery is retained for review.");Check(PowerSetActiveScheme(IntPtr.Zero,ref scheme));
 }
 public static void Validate(string id,string value){var parsed=Parse(value);if(id.Contains("-boost-")&&parsed.Percent>4)throw new InvalidDataException("This processor boost index is outside the documented supported range 0–4.");}
 public static IEnumerable<ValueOption> Choices(string id,string current)=>id.Contains("-boost-")?BoostChoices(current):Choices(current);
 private static IEnumerable<ValueOption> BoostChoices(string current){try{var parsed=Parse(current);return new[]{"Disabled","Enabled","Aggressive","Efficient enabled","Efficient aggressive"}.Select((label,index)=>new ValueOption($"{parsed.Scheme:D}|{index}",label));}catch(InvalidDataException){return [];}}
 public static string Format(string id,string value)=>id.Contains("-boost-")?BoostChoices(value).FirstOrDefault(c=>c.Value==value)?.Label??value:Format(value);
 public static IEnumerable<ValueOption> Choices(string current)
 {try{var(scheme,number)=Parse(current);return new uint[]{0,5,10,25,50,75,90,99,100,number}.Distinct().Order().Select(n=>new ValueOption($"{scheme:D}|{n}",$"{n}%"));}catch(InvalidDataException){return [];}}
 public static string Format(string value){try{var(scheme,n)=Parse(value);return $"{n}% · plan {scheme:D}";}catch(InvalidDataException){return value;}}
 public static readonly OperationDefinition[] Definitions=Ids.Select(id=>new OperationDefinition(id,1,id.Contains("-boost-")?$"Processor boost policy · {(id.EndsWith("-ac")?"plugged in":"battery")}":$"{(id.Contains("-min-")?"Minimum":"Maximum")} processor state · {(id.EndsWith("-ac")?"plugged in":"battery")}","CPU",id.Contains("-boost-")?"Select a documented processor boost policy index on the currently active power scheme.":"Set a documented processor performance-state percentage on the currently active power scheme.","Advanced power tradeoff: high minimums can increase heat; low maximums can reduce performance. Hardware and firmware may limit effect. Excluded from automatic optimization.",id.Contains("-boost-")?"https://learn.microsoft.com/en-us/windows-hardware/customize/power-settings/options-for-perf-state-engine-perfboostmode":Documentation,Evidence.WorkloadDependent,"Advanced · power and thermals")).ToArray();
}
