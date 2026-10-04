using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace ForgePC;

public record Change(string Key, string Label, string Before, string After);
public record PowerPlan(string Id, string Name) { public override string ToString() => Name; }
public sealed class OperationRecord
{
 public string Id { get; set; } = "";
 public int Version { get; set; } = 1;
 public string ValueType { get; set; } = "";
 public string Before { get; set; } = "";
 public string Target { get; set; } = "";
 public string State { get; set; } = "prepared";
 public string Result { get; set; } = "";
 public long DurationMs { get; set; }
}
public sealed class Journal
{
 public int SchemaVersion { get; set; } = 2;
 public string Id { get; set; } = Guid.NewGuid().ToString("N");
 public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
 public string Profile { get; set; } = "";
 public string Status { get; set; } = "prepared";
 public List<Change> Changes { get; set; } = [];
 public List<string> Restored { get; set; } = [];
 public List<OperationRecord> Operations { get; set; } = [];
 public string RecoveryContract { get; set; } = "Exact per-setting undo; no system restore point required";
 public bool IsActive => Status != "restored";
}
public interface IConditionalSettings : ISettings { void WriteIfUnchanged(string key,string expected,string value); }
public interface ISettings { string Read(string key); void Write(string key, string value); }
public interface IJournalStore
{
 IReadOnlyList<string> Warnings { get; }
 bool HasQuarantinedHistory { get; }
 List<Journal> Load();
 void Save(Journal journal);
}
public sealed class JournalStore(string directory) : IJournalStore
{
 private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
 private readonly List<string> warnings = [];
 public IReadOnlyList<string> Warnings => warnings;
 public bool HasQuarantinedHistory => warnings.Count > 0 || (Directory.Exists(Path.Combine(directory, "quarantine")) &&
  Directory.EnumerateFiles(Path.Combine(directory, "quarantine")).Any());
 public static void Validate(Journal j)
 {
  if (!Guid.TryParseExact(j.Id, "N", out _) || j.SchemaVersion != 2) throw new InvalidDataException("Invalid journal identity or schema.");
  if (j.Changes == null || j.Restored == null || j.Operations == null || j.Changes.Count > 256 ||
   j.Operations.Count != j.Changes.Count || j.Profile==null || j.Profile.Length > 120 || j.Changes.Select(c => c.Key).Distinct().Count() != j.Changes.Count || j.Restored.Distinct().Count()!=j.Restored.Count)
   throw new InvalidDataException("Invalid journal structure.");
  foreach (var c in j.Changes) { Catalog.ValidateTarget(c.Key, c.Before); Catalog.ValidateTarget(c.Key, c.After); }
  if (j.Restored.Any(id => !j.Changes.Any(c => c.Key == id))) throw new InvalidDataException("Invalid restored operation.");
  for (var i = 0; i < j.Operations.Count; i++)
  {
   var operation = j.Operations[i]; var change = j.Changes[i];
   if (operation.Id != change.Key || operation.Before != change.Before || operation.Target != change.After ||
    operation.Version != Catalog.Get(operation.Id).Version || operation.ValueType != Catalog.ValueType(operation.Id) ||
    operation.State is not ("prepared" or "applying" or "applied" or "failed" or "rolling back" or "rolled back" or "conflict"))
    throw new InvalidDataException("Invalid operation state.");
   if(j.Restored.Contains(operation.Id)!=(operation.State=="rolled back") || j.Status=="restored" && operation.State!="rolled back")throw new InvalidDataException("Inconsistent restored state.");
  }
  if (j.Status is not ("pending" or "prepared" or "applying" or "applied" or "interrupted" or "restore incomplete" or "rolling back" or "restored"))
   throw new InvalidDataException("Invalid session state.");
 }
 public static Journal Parse(string json)
 {
  using var document = JsonDocument.Parse(json);
  var j = JsonSerializer.Deserialize<Journal>(json) ?? throw new InvalidDataException("Empty journal.");
  if (!document.RootElement.TryGetProperty("SchemaVersion", out var version) || version.GetInt32() == 1)
  {
   j.SchemaVersion = 2;
   j.Operations = j.Changes.Select(c => new OperationRecord {
    Id = c.Key, ValueType = Catalog.ValueType(c.Key), Before = c.Before, Target = c.After,
    State = j.Restored.Contains(c.Key) || j.Status == "restored" ? "rolled back" : j.Status == "applied" ? "applied" : "applying"
   }).ToList();
   if (j.Status == "restored") j.Restored = j.Changes.Select(c => c.Key).ToList();
  }
  // GUID case has no Windows identity meaning. Preserve recovery from older imports
  // while new targets use canonical strings for deterministic read-back comparisons.
  string Canonical(string id,string value){if(id=="power"&&Guid.TryParseExact(value,"D",out var guid))return guid.ToString("D");if(ProcessorPower.Ids.Contains(id)&&value!=null){var parts=value.Split('|');if(parts.Length==2&&Guid.TryParseExact(parts[0],"D",out guid))return guid.ToString("D")+"|"+parts[1];}return value!;}
  if(j.SchemaVersion==2&&j.Changes!=null&&j.Operations!=null){j.Changes=j.Changes.Select(c=>c with{Before=Canonical(c.Key,c.Before),After=Canonical(c.Key,c.After)}).ToList();foreach(var op in j.Operations){op.Before=Canonical(op.Id,op.Before);op.Target=Canonical(op.Id,op.Target);}}
  Validate(j); return j;
 }
 private void EnsureDirectory()
 {
  Directory.CreateDirectory(directory);
  if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Recovery directory cannot be a reparse point.");
 }
 public List<Journal> Load()
 {
  EnsureDirectory(); warnings.Clear(); var result = new List<Journal>();
  // Atomic saves retain the last completed JSON. Orphaned temporary writes are never
  // replayed: Windows is touched only after a successful rename of original state.
  foreach (var path in Directory.GetFiles(directory, "*.json"))
  {
   try
   {
    var file = new FileInfo(path);
    if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length > 2097152) throw new InvalidDataException("Invalid history file.");
    var journal = Parse(File.ReadAllText(path));
    if (!string.Equals(Path.GetFileNameWithoutExtension(path), journal.Id, StringComparison.OrdinalIgnoreCase))
     throw new InvalidDataException("Journal ID does not match its file.");
    result.Add(journal);
   }
   catch (Exception e) when (e is JsonException or InvalidDataException or ArgumentException or NullReferenceException or InvalidOperationException or FormatException or OverflowException)
   {
    var quarantine = Path.Combine(directory, "quarantine"); Directory.CreateDirectory(quarantine);
    if ((File.GetAttributes(quarantine) & FileAttributes.ReparsePoint) != 0) throw new IOException("Quarantine directory is not safe.");
    try { File.Move(path, Path.Combine(quarantine, Guid.NewGuid().ToString("N") + ".bad")); }
    catch (IOException) { }
    catch (UnauthorizedAccessException) { }
    warnings.Add("A malformed history record was quarantined. Valid sessions remain recoverable; applying is blocked until the quarantined record is reviewed.");
   }
   catch (Exception e) when (e is IOException or UnauthorizedAccessException)
   { warnings.Add("A recovery record could not be read. Applying is blocked; other readable sessions remain available."); }
  }
  if (HasQuarantinedHistory && warnings.Count == 0) warnings.Add("Quarantined history needs review before a new session can be applied.");
  return result.OrderByDescending(x => x.CreatedUtc).ToList();
 }
 public void Save(Journal journal)
 {
  Validate(journal); EnsureDirectory();
  var path = Path.Combine(directory, journal.Id + ".json");
  if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Recovery file cannot be a reparse point.");
  var bytes=JsonSerializer.SerializeToUtf8Bytes(journal,Json);if(bytes.Length>2097152)throw new InvalidDataException("Recovery journal exceeds its size limit; no write can begin.");
  var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
  using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
  { stream.Write(bytes); stream.Flush(true); }
  File.Move(temporary, path, true);
 }
}
public record OperationProgress(string SessionId, string OperationId, string State, int Completed, int Total);
public record ExecutionResult(Journal Journal, bool Success, string Message);
public sealed class TuningEngine
{
 private readonly ISettings settings;
 public IJournalStore Store { get; }
 private readonly ICapabilityService capabilities;
 private readonly SemaphoreSlim serial = new(1,1);
 private readonly Action<string,string,string,long> log;
 public TuningEngine(ISettings settings, string directory) : this(settings, new JournalStore(directory), new PermissiveCapabilities()) { }
 public TuningEngine(ISettings settings, IJournalStore store, ICapabilityService capabilities, Action<string,string,string,long>? log = null)
 { this.settings = settings; Store = store; this.capabilities = capabilities; this.log = log ?? ((_,_,_,_) => {}); }
 public List<Journal> History() => Store.Load();
 public Journal Apply(string profile, List<Change> changes)
 {
  var result = ApplyAsync(profile, changes).GetAwaiter().GetResult();
  if (!result.Success) throw new InvalidOperationException(result.Message);
  return result.Journal;
 }
 public async Task<ExecutionResult> ApplyAsync(string profile, List<Change> changes, CancellationToken cancellation = default, IProgress<OperationProgress>? progress = null,bool allowIndependent = false)
 {
  await serial.WaitAsync(cancellation);
  try
  {
   if (changes.Count == 0) throw new InvalidOperationException("There are no changes to apply.");
   var history = Store.Load();
   if (Store.HasQuarantinedHistory) throw new InvalidOperationException("Review quarantined recovery data before starting a new session.");
   var active=history.Where(j=>j.IsActive).ToArray();
   if(active.Length>0&&!allowIndependent)throw new InvalidOperationException("Restore active settings before applying a profile.");
   if(allowIndependent&&active.Any(j=>j.Status!="applied"))throw new InvalidOperationException("Resolve interrupted recovery before applying another setting.");
   static string Group(string id)=>id=="power"||ProcessorPower.Ids.Contains(id)?"power-and-cpu":id;
   var occupied=active.SelectMany(j=>j.Operations.Where(o=>o.State!="rolled back")).Select(o=>Group(o.Id)).ToHashSet();
   if(changes.Any(c=>occupied.Contains(Group(c.Key))))throw new InvalidOperationException("Restore the existing change for this setting or power group in Recovery first.");
   if (changes.Select(c => c.Key).Distinct().Count() != changes.Count) throw new InvalidDataException("Duplicate operations are not allowed.");
   changes=OperationOrdering.Order(changes);
   foreach (var c in changes) Validate(c);
   var j = new Journal { Profile = profile, Changes = changes.ToList(), Operations = changes.Select(c => new OperationRecord { Id = c.Key, ValueType = Catalog.ValueType(c.Key), Before = c.Before, Target = c.After }).ToList() };
   Store.Save(j); // All originals and targets are durable before the first write.
   try
   {
    for (var i = 0; i < changes.Count; i++)
    {
     cancellation.ThrowIfCancellationRequested();
     var c = changes[i]; var op = j.Operations[i]; var watch = Stopwatch.StartNew();
     Validate(c); // Applicability, policy and preview are rechecked at each boundary.
     j.Status = "applying"; op.State = "applying"; Store.Save(j);
     progress?.Report(new(j.Id,c.Key,op.State,i,changes.Count));
     if(settings is IConditionalSettings conditional)conditional.WriteIfUnchanged(c.Key,c.Before,c.After);else settings.Write(c.Key,c.After);
     if (settings.Read(c.Key) != c.After) throw new IOException("Write verification failed.");
     op.State = "applied"; op.Result = "Read-back verified"; op.DurationMs = watch.ElapsedMilliseconds;
     Store.Save(j); log(j.Id,c.Key,"applied and verified",op.DurationMs);
     progress?.Report(new(j.Id,c.Key,op.State,i+1,changes.Count));
    }
    j.Status = "applied"; Store.Save(j);
    return new(j,true,"Session applied and verified. Exact originals are saved.");
   }
   catch (Exception error)
   {
    var cancelled = error is OperationCanceledException;
    j.Status = "interrupted";
    try { Store.Save(j); } catch(Exception saveError) { log(j.Id,"journal","intent-save failed:"+saveError.GetType().Name,0); /* Previous intent remains durable. */ }
    log(j.Id,"apply",error.GetType().Name+":"+error.HResult.ToString("X8"),0);
    foreach(var attempted in j.Operations.Where(o=>o.State=="applying"))attempted.Result="Apply failed: "+error.GetType().Name+"; recovery attempted";
    var problems = RestoreCore(j,progress);
    return new(j,false,(cancelled ? "Cancelled at a safe boundary." : "Apply stopped.") +
     (problems.Count == 0 ? " All attempted changes were restored." : " Some settings need recovery review.") + " Session " + j.Id[..8] + ".");
   }
  }
  finally { serial.Release(); }
 }
 private void Validate(Change c)
 {
  Catalog.ValidateTarget(c.Key,c.Before); Catalog.ValidateTarget(c.Key,c.After);
  var cap = capabilities.Check(c.Key,c.After);
  if (!cap.Eligible) throw new InvalidOperationException(c.Label + ": " + cap.Reason);
  if (settings.Read(c.Key) != c.Before) throw new InvalidOperationException("The preview is stale. Refresh and review again.");
 }
 public List<string> Restore(Journal journal) { serial.Wait(); try { return RestoreCore(journal,null); } finally { serial.Release(); } }
 public async Task<List<string>> RestoreAsync(Journal journal, IProgress<OperationProgress>? progress = null)
 { await serial.WaitAsync(); try { return RestoreCore(journal,progress); } finally { serial.Release(); } }
 public async Task<List<string>> RestoreSelectedAsync(Journal journal,IReadOnlySet<string> selected,IProgress<OperationProgress>? progress=null)
 {if(selected.Count==0||selected.Any(id=>!journal.Changes.Any(c=>c.Key==id)))throw new InvalidDataException("Select settings from this session.");await serial.WaitAsync();try{return RestoreCore(journal,progress,selected);}finally{serial.Release();}}
 private List<string> RestoreCore(Journal journal, IProgress<OperationProgress>? progress,IReadOnlySet<string>? selected=null)
 {
  JournalStore.Validate(journal);
  var issues = new List<string>();
  for (var i = journal.Operations.Count - 1; i >= 0; i--)
  {
   var op = journal.Operations[i];
   if(selected!=null&&!selected.Contains(op.Id))continue;
   if (op.State == "rolled back" || journal.Restored.Contains(op.Id)) continue;
   if (op.State == "prepared") { op.State = "rolled back"; journal.Restored.Add(op.Id); continue; }
   try
   {
    var current = settings.Read(op.Id);
    if (current != op.Before && current != op.Target)
    { op.State = "conflict"; op.Result = "Changed outside EZoptimizer; current value preserved"; issues.Add(Catalog.Get(op.Id).Title + " has an external conflict."); Store.Save(journal); continue; }
    if (current != op.Before)
    {
     var cap = capabilities.CheckRestore(op.Id,op.Before);
     if (!cap.Eligible) throw new InvalidOperationException("Current capability or policy prevents restoration.");
     journal.Status = "rolling back"; op.State = "rolling back"; Store.Save(journal);
     if(settings is IConditionalSettings conditional)conditional.WriteIfUnchanged(op.Id,current,op.Before);else settings.Write(op.Id,op.Before);
    }
    if (settings.Read(op.Id) != op.Before) throw new IOException("Restore verification failed.");
    op.State = "rolled back"; op.Result = "Original value verified"; journal.Restored.Add(op.Id); Store.Save(journal);
    log(journal.Id,op.Id,"restored and verified",op.DurationMs);
    progress?.Report(new(journal.Id,op.Id,op.State,journal.Restored.Count,journal.Operations.Count));
   }
   catch
   { op.State = "failed"; journal.Restored.Remove(op.Id); op.Result = "Could not verify original value or persist recovery state"; issues.Add(Catalog.Get(op.Id).Title + " could not be restored. Retry recovery."); try { Store.Save(journal); } catch(Exception error) { log(journal.Id,op.Id,"recovery-save failed:"+error.GetType().Name,0); } }
  }
  journal.Status = issues.Count == 0 && journal.Operations.All(op=>op.State=="rolled back") ? "restored" : "restore incomplete";
  try { Store.Save(journal); } catch { issues.Add("Recovery state could not be saved. Keep the history folder and retry."); journal.Status = "restore incomplete"; }
  return issues;
 }
}
