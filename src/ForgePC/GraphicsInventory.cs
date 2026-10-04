using System.Runtime.InteropServices;
namespace ForgePC;

public record GraphicsAdapter(string Model,string Vendor,ulong DedicatedVideoBytes,ulong SharedSystemBytes);
public static class GraphicsInventory
{
 [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct Description
 {
  [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string Name;
  public uint Vendor,Device,Subsystem,Revision;public nuint VideoMemory,SystemMemory,SharedMemory;public uint LuidLow;public int LuidHigh;public uint Flags;
 }
 [DllImport("dxgi.dll",ExactSpelling=true)]private static extern int CreateDXGIFactory1(ref Guid iid,out IntPtr factory);
 [UnmanagedFunctionPointer(CallingConvention.StdCall)]private delegate int Enumerate(IntPtr self,uint index,out IntPtr adapter);
 [UnmanagedFunctionPointer(CallingConvention.StdCall)]private delegate int GetDescription(IntPtr self,out Description description);
 private static T Method<T>(IntPtr instance,int slot) where T:Delegate=>Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance),slot*IntPtr.Size));
 public static IReadOnlyList<GraphicsAdapter> Read()
 {
  var iid=new Guid("770aae78-f26f-4dba-a829-253c83d1b387");Marshal.ThrowExceptionForHR(CreateDXGIFactory1(ref iid,out var factory));var result=new List<GraphicsAdapter>();
  try{var enumerate=Method<Enumerate>(factory,12);for(uint index=0;index<32;index++){var code=enumerate(factory,index,out var adapter);if(code==unchecked((int)0x887A0002))break;Marshal.ThrowExceptionForHR(code);try{Marshal.ThrowExceptionForHR(Method<GetDescription>(adapter,10)(adapter,out var description));if((description.Flags&2)!=0)continue;result.Add(new(description.Name,description.Vendor switch{0x10de=>"NVIDIA",0x1002=>"AMD",0x8086=>"Intel",_=>"Vendor 0x"+description.Vendor.ToString("X4")},(ulong)description.VideoMemory,(ulong)description.SharedMemory));}finally{Marshal.Release(adapter);}}}finally{Marshal.Release(factory);}return result;
 }
}
