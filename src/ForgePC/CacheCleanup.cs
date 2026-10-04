using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ForgePC;

public record CacheSpec(string Id,string Name,string Root,string Pattern,int MinimumAgeDays,bool Recursive,bool Administrator,string Note);
public record CacheFile(string Path,long Bytes,long Modified);
public record CacheSnapshot(List<CacheFile> Files,int Skipped,bool Partial);
public record CacheOutcome(string Id,bool Success,int FilesBefore,long BytesBefore,int FilesRemoved,int FilesSkipped,long BytesRemoved,int FilesAfter,long BytesAfter,bool Partial,string Message);

// No supplied paths cross the elevation boundary. The helper resolves this fixed catalog itself.
public static class CacheCatalog
{
 public static CacheSpec[] All => [
  new("user-temp","User Temp",Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Temp"),"*",7,true,false,"Files older than 7 days. Permanent deletion; locked files are skipped."),
  new("windows-temp","Windows Temp",Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"Temp"),"*",7,true,true,"Files older than 7 days. Administrator required; locked files are skipped."),
  new("shader-cache","DirectX shader cache",Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"D3DSCache"),"*",1,true,false,"Older cache files only. Shaders rebuild; the next game launch may stutter."),
  new("prefetch","Prefetch · troubleshooting",Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"Prefetch"),"*.pf",0,false,true,"Troubleshooting only. Deletes .pf files; subsequent app launches may be slower."),
  new("wer-archive","Windows Error Reporting archive",Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Microsoft","Windows","WER","ReportArchive"),"*",7,true,false,"Reports older than 7 days. Deletes diagnostic evidence permanently; keep it while troubleshooting."),
  new("wer-queue","Windows Error Reporting queue",Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Microsoft","Windows","WER","ReportQueue"),"*",7,true,false,"Queued reports older than 7 days. Removes pending diagnostic submissions; no performance gain claimed."),
  new("crash-dumps","User crash dumps",Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CrashDumps"),"*.dmp",7,false,false,"Dump files older than 7 days. Permanent loss of troubleshooting evidence; excluded from automatic recommendations."),
  new("thumbnails","Thumbnail cache",Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Microsoft","Windows","Explorer"),"thumbcache_*.db",1,false,false,"Older thumbnail databases only. Locked databases stay; thumbnails rebuild.")
 ];
 public static CacheSpec Get(string id)=>All.SingleOrDefault(x=>x.Id==id)??throw new InvalidDataException("Unknown cleanup category.");
}

public sealed class CacheCleaner(CacheSpec spec)
{
 private readonly string root=Path.GetFullPath(spec.Root).TrimEnd(Path.DirectorySeparatorChar);
 private static bool Within(string path,string folder)=>path.StartsWith(folder.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase);
 private void ValidateRoot()
 {
  if(root.StartsWith(@"\\")||root.Length<4||root==Path.GetPathRoot(root)?.TrimEnd('\\'))throw new InvalidDataException("A local cache folder is required.");
  foreach(var folder in new[]{Environment.SpecialFolder.UserProfile,Environment.SpecialFolder.DesktopDirectory,Environment.SpecialFolder.MyDocuments,Environment.SpecialFolder.MyPictures,Environment.SpecialFolder.MyMusic,Environment.SpecialFolder.MyVideos})
  {var protectedPath=Environment.GetFolderPath(folder);if(protectedPath.Length>0&&(root.Equals(protectedPath,StringComparison.OrdinalIgnoreCase)||Within(protectedPath,root)||(folder!=Environment.SpecialFolder.UserProfile&&Within(root,protectedPath))))throw new InvalidDataException("Personal folders are excluded.");}
  var downloads=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Downloads");if(root.Equals(downloads,StringComparison.OrdinalIgnoreCase)||Within(root,downloads))throw new InvalidDataException("Downloads are excluded.");
  CheckAncestors(root);
 }
 private static void CheckAncestors(string path)
 {for(var current=path;!string.IsNullOrEmpty(current);current=Path.GetDirectoryName(current))if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked folders are excluded.");}
 public CacheSnapshot Scan()
 {
  if(!Directory.Exists(root))return new([],0,false);
  ValidateRoot();var files=new List<CacheFile>();var pending=new Stack<string>();pending.Push(root);var timer=Stopwatch.StartNew();int inspected=0,skipped=0;bool partial=false;
  while(pending.TryPop(out var folder))
  {
   if(timer.Elapsed.TotalSeconds>5||inspected>=25000){partial=true;break;}
   try{CheckAncestors(folder);foreach(var path in Directory.EnumerateFileSystemEntries(folder))
   {
    if(++inspected>25000||timer.Elapsed.TotalSeconds>5){partial=true;break;}
    try{var info=new FileInfo(path);if((info.Attributes&FileAttributes.ReparsePoint)!=0){skipped++;continue;}
     if((info.Attributes&FileAttributes.Directory)!=0){if(spec.Recursive&&Path.GetRelativePath(root,path).Count(c=>c=='\\')<8)pending.Push(path);continue;}
     if(!System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(spec.Pattern,info.Name,true))continue;
     if(info.LastWriteTimeUtc>DateTime.UtcNow.AddDays(-spec.MinimumAgeDays)){skipped++;continue;}
     files.Add(new(path,info.Length,info.LastWriteTimeUtc.ToFileTimeUtc()));
    }catch(Exception e)when(e is IOException or UnauthorizedAccessException){skipped++;partial=true;}
   }}catch(Exception e)when(e is IOException or UnauthorizedAccessException){skipped++;partial=true;}
  }
  return new(files,skipped,partial);
 }
 public CacheOutcome Execute(bool clean)
 {
  var before=Scan();int removed=0,skipped=before.Skipped;long bytes=0;
  if(clean)foreach(var file in before.Files){try{DeleteVerified(file);removed++;bytes+=file.Bytes;}catch(Exception e)when(e is IOException or UnauthorizedAccessException or Win32Exception or InvalidDataException){skipped++;}}
  var after=clean?Scan():before;var partial=before.Partial||after.Partial;
  bool success=!clean?!partial:removed==before.Files.Count&&!partial;
  return new(spec.Id,success,before.Files.Count,before.Files.Sum(f=>f.Bytes),removed,skipped,bytes,after.Files.Count,after.Files.Sum(f=>f.Bytes),partial,
   !clean?(partial?"Partial scan; some paths could not be read.":"Scan complete."):before.Files.Count==0?"No eligible files found.":$"{removed} files removed; {skipped} skipped; {after.Files.Count} eligible files remain."+(partial?" Partial bounded scan.":""));
 }
 // Open the exact object without following a final symlink, deny writers/deleters, then check physical path and metadata.
 internal void DeleteVerified(CacheFile file)
 {
  ValidateRoot();var path=Path.GetFullPath(file.Path);if(!Within(path,root))throw new InvalidDataException("File escaped cleanup root.");CheckAncestors(Path.GetDirectoryName(path)!);
  if(!System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(spec.Pattern,Path.GetFileName(path),true)||(!spec.Recursive&&Path.GetDirectoryName(path)!=root))throw new InvalidDataException("File does not match this cache category.");
  using(var handle=CreateFile(path,0x80010000,1,IntPtr.Zero,3,0x00200000,IntPtr.Zero))
  {
   if(handle.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
   var final=new System.Text.StringBuilder(32768);uint length=GetFinalPathNameByHandle(handle,final,32768,0);if(length==0||length>=32768)throw new IOException("Physical path unavailable.");var actual=final.ToString();if(actual.StartsWith(@"\\?\"))actual=actual[4..];
   if(!Within(actual,root)||!actual.Equals(path,StringComparison.OrdinalIgnoreCase))throw new IOException("Physical path changed.");
   if(!GetFileInformationByHandle(handle,out var info))throw new Win32Exception(Marshal.GetLastWin32Error());
   var modified=((long)info.Write.High<<32)|info.Write.Low;var size=((long)info.SizeHigh<<32)|info.SizeLow;
   if((info.Attributes&0x410)!=0||info.Links!=1||size!=file.Bytes||modified!=file.Modified||DateTime.FromFileTimeUtc(modified)>DateTime.UtcNow.AddDays(-spec.MinimumAgeDays))throw new IOException("File changed, linked, or no longer eligible.");
   byte disposition=1;if(!SetFileInformationByHandle(handle,4,ref disposition,1))throw new Win32Exception(Marshal.GetLastWin32Error());
  }
  try{File.GetAttributes(path);throw new IOException("Deletion not verified.");}catch(FileNotFoundException){}catch(DirectoryNotFoundException){}
 }
 [StructLayout(LayoutKind.Sequential)] private struct FileTime{public uint Low,High;}
 [StructLayout(LayoutKind.Sequential)] private struct FileInfoNative{public uint Attributes;public FileTime Creation,Access,Write;public uint Volume,SizeHigh,SizeLow,Links,IndexHigh,IndexLow;}
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern SafeFileHandle CreateFile(string name,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle,System.Text.StringBuilder path,uint size,uint flags);
 [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle,out FileInfoNative info);
 [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool SetFileInformationByHandle(SafeFileHandle handle,int informationClass,ref byte value,uint size);
}
