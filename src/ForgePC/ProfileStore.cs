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
 public static string NormalizeFocus(string name)=>name switch {"Competitive Gaming"=>"Competitive","Maximum Performance"=>"Performance","Everyday"=>"Balanced",_=>name};
 public static ProfileDocument FromRecommendations(string name,IEnumerable<Recommendation> recommendations,bool safeOnly=false)=>new(1,name,recommendations.Where(r=>!r.Optimized&&(!safeOnly||RecommendationEngine.SafeForAutomatic(r.Id))).Select(r=>new ProfileParameter(r.Id,Catalog.Get(r.Id).Version,r.Target)).ToList());

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
