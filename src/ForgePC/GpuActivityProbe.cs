using System.Runtime.InteropServices;
namespace ForgePC;

// PDH supplies formatted percentages; independent GPU engines are not added together.
public sealed class GpuActivityProbe : IDisposable
{
 private IntPtr query,counter;private bool baseline,disposed;private readonly object gate=new();
 [StructLayout(LayoutKind.Explicit,Size=16)] private struct CounterValue{[FieldOffset(0)]public uint Status;[FieldOffset(8)]public double Number;}
 [StructLayout(LayoutKind.Sequential)] private struct CounterItem{public IntPtr Name;public CounterValue Value;}
 [DllImport("pdh.dll",CharSet=CharSet.Unicode)] private static extern uint PdhOpenQuery(string? source,IntPtr data,out IntPtr query);
 [DllImport("pdh.dll",CharSet=CharSet.Unicode)] private static extern uint PdhAddEnglishCounter(IntPtr query,string path,IntPtr data,out IntPtr counter);
 [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
 [DllImport("pdh.dll",CharSet=CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArray(IntPtr counter,uint format,ref uint bytes,out uint count,IntPtr items);
 [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
 public static double? Aggregate(IEnumerable<(string Name,double Value)> samples)
 {
  var values=samples.Where(s=>s.Name.Contains("_luid_")&&double.IsFinite(s.Value)&&s.Value>=0).GroupBy(s=>s.Name.Split("_luid_")[1]).Select(g=>Math.Clamp(g.Sum(s=>s.Value),0,100)).ToArray();return values.Length==0?null:values.Max();
 }
 public string Read()
 {
  lock(gate)
  {
   if(disposed)return "Unavailable";
   if(query==IntPtr.Zero){if(PdhOpenQuery(null,IntPtr.Zero,out query)!=0)return "Unavailable";if(PdhAddEnglishCounter(query,@"\GPU Engine(*)\Utilization Percentage",IntPtr.Zero,out counter)!=0){PdhCloseQuery(query);query=IntPtr.Zero;return "Unavailable";}}
   if(PdhCollectQueryData(query)!=0)return "Unavailable";if(!baseline){baseline=true;return "Warming up";}
   uint bytes=0;var status=PdhGetFormattedCounterArray(counter,0x200,ref bytes,out var count,IntPtr.Zero);if(status!=0x800007D2||bytes==0||bytes>1048576||count>8192)return "Unavailable";
   var buffer=Marshal.AllocHGlobal((int)bytes);try
   {
    if(PdhGetFormattedCounterArray(counter,0x200,ref bytes,out count,buffer)!=0||count>8192)return "Unavailable";var size=Marshal.SizeOf<CounterItem>();if((ulong)count*(uint)size>bytes)return "Unavailable";
    var samples=new List<(string,double)>();for(int i=0;i<count;i++){var item=Marshal.PtrToStructure<CounterItem>(IntPtr.Add(buffer,i*size));if(item.Value.Status is 0 or 1&&item.Name!=IntPtr.Zero)samples.Add((Marshal.PtrToStringUni(item.Name)??"",item.Value.Number));}
    var usage=Aggregate(samples);return usage is double percent?$"{percent:0}% busiest engine":"Unavailable";
   }finally{Marshal.FreeHGlobal(buffer);}
  }
 }
 public void Dispose(){lock(gate){if(disposed)return;disposed=true;if(query!=IntPtr.Zero)PdhCloseQuery(query);query=IntPtr.Zero;}}
}
