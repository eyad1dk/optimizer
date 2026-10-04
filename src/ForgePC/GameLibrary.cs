using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForgePC;
public record SavedGame(string Id,string Name,string Executable,string Profile,bool Automatic=false)
{public override string ToString()=>Name;}
public record GameDocument(int Schema,List<SavedGame> Games);
public sealed class GameLibrary(string path)
{
 private static readonly JsonSerializerOptions Json=new(){WriteIndented=true,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
 public static GameDocument Parse(string text)
 {
  if(text.Length>65536)throw new InvalidDataException("Game library exceeds 64 KB.");var document=JsonSerializer.Deserialize<GameDocument>(text,Json)??throw new InvalidDataException("Invalid game library.");
  if(document.Schema!=1||document.Games==null||document.Games.Count>100||document.Games.Any(g=>g==null)||document.Games.Select(g=>g.Id).Distinct().Count()!=document.Games.Count)throw new InvalidDataException("Unsupported game library.");
  foreach(var game in document.Games){if(!Guid.TryParseExact(game.Id,"N",out _)||string.IsNullOrWhiteSpace(game.Name)||game.Name.Length>80||game.Executable==null||game.Executable.Length>4096||!Path.IsPathFullyQualified(game.Executable)||game.Executable.StartsWith(@"\\")||!Path.GetExtension(game.Executable).Equals(".exe",StringComparison.OrdinalIgnoreCase)||game.Profile is not("Gaming" or "Competitive Gaming" or "Balanced" or "Everyday" or "Maximum Performance" or "Quiet" or "Battery Saver" or "Low-End PC" or "Coding" or "Custom"))throw new InvalidDataException("Invalid game path or profile.");}
  return document;
 }
 public List<SavedGame> Load(){if(!File.Exists(path))return [];if(new FileInfo(path).Length>65536)throw new InvalidDataException("Game library exceeds 64 KB.");return Parse(File.ReadAllText(path)).Games;}
 public void Save(IEnumerable<SavedGame> games)
 {if(File.Exists(path))Load();var text=JsonSerializer.Serialize(new GameDocument(1,games.ToList()),Json);Parse(text);Directory.CreateDirectory(Path.GetDirectoryName(path)!);using(var stream=new FileStream(path+".tmp",FileMode.Create,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))using(var writer=new StreamWriter(stream)){writer.Write(text);writer.Flush();stream.Flush(true);}File.Move(path+".tmp",path,true);}
}
