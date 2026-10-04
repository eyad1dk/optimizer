using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
namespace ForgePC;
public static class PointerAcceleration
{
 public const string Id="mouse-acceleration";
 [StructLayout(LayoutKind.Sequential)] private struct MouseInfo{public int First,Second,Level;}
 [DllImport("user32.dll",EntryPoint="SystemParametersInfoW",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool MouseParameter(uint action,uint parameter,ref MouseInfo info,uint flags);
 private static MouseInfo ParseInfo(string value){var parts=value.Split(',');if(parts.Length!=3)throw new InvalidDataException("Invalid mouse acceleration state.");var numbers=parts.Select(p=>{if(!int.TryParse(p,NumberStyles.None,CultureInfo.InvariantCulture,out var n)||n<0||n.ToString(CultureInfo.InvariantCulture)!=p)throw new InvalidDataException("Invalid mouse parameter.");return n;}).ToArray();if(numbers[2]>2)throw new InvalidDataException("Unsupported acceleration level.");return new(){First=numbers[0],Second=numbers[1],Level=numbers[2]};}
 public static void Validate(string value)=>ParseInfo(value);
 public static string Read(){var info=new MouseInfo();if(!MouseParameter(3,0,ref info,0))throw new Win32Exception(Marshal.GetLastWin32Error());var value=$"{info.First},{info.Second},{info.Level}";Validate(value);return value;}
 public static void Write(string value){var info=ParseInfo(value);if(!MouseParameter(4,0,ref info,3))throw new Win32Exception(Marshal.GetLastWin32Error());}
 public static string Format(string value){try{var info=ParseInfo(value);return $"{(info.Level==0?"Off":"Level "+info.Level)} · saved thresholds {info.First}, {info.Second}";}catch(InvalidDataException){return value;}}
 public static IEnumerable<ValueOption> Choices(string current){try{var info=ParseInfo(current);return Enumerable.Range(0,3).Select(level=>new ValueOption($"{info.First},{info.Second},{level}",level==0?"Acceleration off":"Acceleration level "+level));}catch(InvalidDataException){return [];}}
 public static readonly OperationDefinition Definition=new(Id,1,"Windows mouse acceleration","Windows","Choose the documented Windows pointer acceleration level while preserving both existing threshold values.","Changes desktop pointer behavior. Raw-input games may ignore it; it does not change mouse polling rate or guarantee lower input latency. Kept out of presets.",NativePreferences.Documentation);
}
