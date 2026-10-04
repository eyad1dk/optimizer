using System.Globalization;
using System.IO;
using System.Diagnostics;

namespace ForgePC;

public record FrameSummary(int Frames, double AverageFps, double LowOnePercent, double P95, double P99)
{
 public override string ToString() => $"{Frames:N0} frames · average {AverageFps:0.0} FPS · 1% low {LowOnePercent:0.0} FPS\n95th / 99th percentile frame time: {P95:0.00} / {P99:0.00} ms";
}
public static class FrameBenchmark
{
 public static FrameSummary Parse(string csv)
 {
  if(csv.Length>8*1024*1024)throw new InvalidDataException("CSV exceeds 8 MB.");
  using var reader=new StringReader(csv);var header=Fields(reader.ReadLine()??"");
  var columns=header.Select((name,index)=>(name,index)).Where(x=>x.name is "FrameTimeMs" or "MsBetweenPresents").ToArray();
  if(columns.Length!=1)throw new InvalidDataException("CSV needs exactly one FrameTimeMs or MsBetweenPresents column, in milliseconds.");
  var streamColumns=header.Select((name,index)=>(name,index)).Where(x=>x.name is "ProcessID" or "SwapChainAddress" or "Application").Select(x=>x.index).ToArray();
  var values=new List<double>();string? stream=null,line;
  while((line=reader.ReadLine())!=null)
  {
   if(string.IsNullOrWhiteSpace(line))continue;
   if(values.Count>=200000)throw new InvalidDataException("CSV exceeds 200,000 frames.");
   var row=Fields(line);if(row.Length!=header.Length)throw new InvalidDataException("CSV rows must match the header.");
   var identity=string.Join("\u001f",streamColumns.Select(i=>row[i]));
   if(stream!=null&&identity!=stream)throw new InvalidDataException("Export one process and swap chain per CSV before comparing.");stream=identity;
   if(!double.TryParse(row[columns[0].index],NumberStyles.Float,CultureInfo.InvariantCulture,out var value)||!double.IsFinite(value)||value<=0||value>60000)
    throw new InvalidDataException("Frame times must be positive finite milliseconds, at most 60,000.");
   values.Add(value);
  }
  if(values.Count<30)throw new InvalidDataException("At least 30 valid frames are required.");
  values.Sort();double Percentile(double p)=>values[(int)Math.Ceiling(values.Count*p)-1];
  return new(values.Count,1000/values.Average(),1000/values.TakeLast((int)Math.Ceiling(values.Count*.01)).Average(),Percentile(.95),Percentile(.99));
 }
 private static string[] Fields(string line)
 {
  var fields=new List<string>();var value=new System.Text.StringBuilder();bool quoted=false,closed=false;
  for(int i=0;i<line.Length;i++)
  {
   var c=line[i];
   if(quoted){if(c=='"'){if(i+1<line.Length&&line[i+1]=='"'){value.Append('"');i++;}else{quoted=false;closed=true;}}else value.Append(c);}
   else if(c==','){fields.Add(value.ToString().Trim());value.Clear();closed=false;}
   else if(c=='"'&&value.Length==0&&!closed)quoted=true;
   else if(closed||c=='"')throw new InvalidDataException("Malformed CSV quoting.");
   else value.Append(c);
  }
  if(quoted)throw new InvalidDataException("Multiline or unclosed CSV fields are unsupported.");
  fields.Add(value.ToString().Trim().TrimStart('\uFEFF'));return fields.ToArray();
 }
}

public static class WorkspaceInspector
{
 public static string Inspect(string path)
 {
  path=Path.GetFullPath(path);
  if(path.StartsWith(@"\\")||(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Choose a local folder that is not a link.");
  var clock=Stopwatch.StartNew();int visited=0,skipped=0;long bytes=0;bool bounded=false;
  var stack=new Stack<(string Path,int Depth,bool Generated)>();stack.Push((path,0,false));
  var names=new HashSet<string>(["node_modules","bin","obj",".venv",".next","target",".gradle","dist"],StringComparer.OrdinalIgnoreCase);
  while(stack.TryPop(out var item))
  {
   if(clock.Elapsed.TotalSeconds>3||visited>=25000){bounded=true;break;}
   try{foreach(var entry in Directory.EnumerateFileSystemEntries(item.Path))
   {
    if(clock.Elapsed.TotalSeconds>3||++visited>25000){bounded=true;break;}
    try{var attributes=File.GetAttributes(entry);if((attributes&FileAttributes.ReparsePoint)!=0){skipped++;continue;}
     if((attributes&FileAttributes.Directory)!=0){if(item.Depth>=8){skipped++;bounded=true;}else stack.Push((entry,item.Depth+1,item.Generated||names.Contains(Path.GetFileName(entry))));}
     else if(item.Generated)bytes+=new FileInfo(entry).Length;
    }catch(IOException){skipped++;}catch(UnauthorizedAccessException){skipped++;}
   }}catch(IOException){skipped++;}catch(UnauthorizedAccessException){skipped++;}
  }
  return $"{visited:N0} entries inspected · {bytes/1048576d:N1} MB in common dependency/output folders.\n{(bounded?"Partial scan: a time, depth or entry limit was reached.":"Scan completed within limits.")} {skipped} inaccessible or linked entries skipped.\nLogical file sizes can include duplicates. Folder names alone do not prove files are safe to remove. No files were changed.";
 }
}
