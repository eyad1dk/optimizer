using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace ForgePC;
public record ProfileParameter(string Id, int Version, string Value);
public sealed record ProfileDocument(int SchemaVersion, string Name, List<ProfileParameter> Parameters);
public sealed class ProfileStore
{
 private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
 public static ProfileDocument Parse(string content)
 {
  if (content.Length > 65536) throw new InvalidDataException("Profile is too large.");
  var result = JsonSerializer.Deserialize<ProfileDocument>(content,Json) ?? throw new InvalidDataException("Profile is empty.");
  if (result.SchemaVersion != 1 || string.IsNullOrWhiteSpace(result.Name) || result.Name.Length > 60 ||
   result.Parameters == null || result.Parameters.Count > 256 ||
   result.Parameters.Any(p=>p==null) || result.Parameters.Select(p => p.Id).Distinct().Count() != result.Parameters.Count)
   throw new InvalidDataException("Unsupported or malformed profile.");
  foreach (var parameter in result.Parameters)
  {
   if (parameter.Version != Catalog.Get(parameter.Id).Version) throw new InvalidDataException("Unsupported operation version.");
   Catalog.ValidateTarget(parameter.Id,parameter.Value);
  }
  return result;
 }
 public static string Serialize(ProfileDocument profile) { var content = JsonSerializer.Serialize(profile,Json); Parse(content); return content; }
 public static ProfileDocument BuiltIn(string name)
 {
  if(name=="Custom")return new(1,name,[]);
  var plan=name is "Quiet" or "Battery Saver"?"a1841308-3541-4fab-bc81-f71556f20b4a":name=="Maximum Performance"?"8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c":"381b4222-f694-41f0-9685-ff5bb260df2e";
  var result=new ProfileDocument(1,name,[new("animations",1,name is "Everyday" or "Balanced"?"On":"Off"),new("menus",1,name is "Everyday" or "Balanced"?"On":"Off"),new("power",1,plan)]);
  if(name is "Competitive Gaming" or "Low-End PC")foreach(var id in new[]{"minimize-animation","combo-animation","tooltip-animation","selection-fade"})result.Parameters.Add(new(id,1,"Off"));
  return result;
 }

}
public sealed class QueueStore(string path)
{
 public Dictionary<string,string> Load()
 {
  if (!File.Exists(path)) return [];
  var profile = ProfileStore.Parse(File.ReadAllText(path));
  return profile.Parameters.ToDictionary(p => p.Id,p => p.Value);
 }
 public void Save(IEnumerable<KeyValuePair<string,string>> entries)
 {
  var profile = new ProfileDocument(1,"Review queue",entries.Select(p => new ProfileParameter(p.Key,1,p.Value)).ToList());
  var content = ProfileStore.Serialize(profile);
  Directory.CreateDirectory(Path.GetDirectoryName(path)!);
  var temp = path + ".tmp";
  using (var stream = new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
  using (var writer = new StreamWriter(stream)) { writer.Write(content); writer.Flush(); stream.Flush(true); }
  File.Move(temp,path,true);
 }
}
