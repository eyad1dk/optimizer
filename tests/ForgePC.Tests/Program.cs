using ForgePC;

using System.Text.Json;

var root = Path.Combine(Path.GetTempPath(),"EZoptimizer-regression-" + Guid.NewGuid().ToString("N"));

Directory.CreateDirectory(root);

int passed = 0;

void Test(string title, Action body) { body(); passed++; Console.WriteLine("PASS " + title); }

void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed."); }

void Throws<T>(Action action) where T:Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }

(FakeSettings Settings,TuningEngine Engine,JournalStore Store) Fixture()

{

 var settings = new FakeSettings(); var store = new JournalStore(Path.Combine(root,Guid.NewGuid().ToString("N")));

 return (settings,new TuningEngine(settings,store,new PermissiveCapabilities()),store);

}

List<Change> Changes() => [new("animations","Animations","On","Off"),new("menus","Menus","On","Off")];

try

{

 Test("originals are durable and survive a service restart",() => {

  var (s,e,store)=Fixture(); e.Apply("Gaming",Changes()); Assert(s.Values["menus"]=="Off");

  Assert(e.History().Single().Changes.Count==2); Assert(new TuningEngine(s,store,new PermissiveCapabilities()).Restore(store.Load().Single()).Count==0);

  Assert(s.Values["menus"]=="On"); Assert(e.History().Single().Status=="restored");

 });

 Test("stale preview causes zero writes",() => {

  var (s,e,_) = Fixture(); s.Values["menus"]="Off"; Throws<InvalidOperationException>(()=>e.Apply("Coding",Changes())); Assert(s.Writes==0);

 });

 Test("failed write automatically compensates earlier writes",() => {

  var (s,e,_) = Fixture(); s.FailKey="menus"; Throws<InvalidOperationException>(()=>e.Apply("Gaming",Changes()));

  Assert(s.Values["animations"]=="On"); Assert(e.History().Single().Status=="restored");

 });

 Test("interruption after a write leaves retryable recovery",() => {

  var (s,e,_) = Fixture(); s.WriteThenFail=true; Throws<InvalidOperationException>(()=>e.Apply("Coding",Changes()));

  s.WriteThenFail=false; Assert(e.Restore(e.History().Single()).Count==0); Assert(s.Values["animations"]=="On");

 });

 Test("undo preserves external changes and supports retry",() => {

  var (s,e,_) = Fixture(); e.Apply("Gaming",Changes()); s.Values["menus"]="External";

  Assert(e.Restore(e.History().Single()).Count==1); Assert(s.Values["menus"]=="External"); Assert(s.Values["animations"]=="On");

  s.Values["menus"]="Off"; Assert(e.Restore(e.History().Single()).Count==0);

 });

 Test("one active session and idempotent undo",() => {

  var (s,e,_) = Fixture(); var j=e.Apply("Gaming",Changes());

  Throws<InvalidOperationException>(()=>e.Apply("Coding",[new("animations","Animations","Off","On")]));

  e.Restore(j); var count=s.Writes; e.Restore(j); Assert(count==s.Writes); e.Apply("Coding",Changes()); Assert(e.History().Count==2);

 });

 Test("silent write rejection is verified and compensated",() => {

  var (s,e,_) = Fixture(); s.IgnoreWrites=true; Throws<InvalidOperationException>(()=>e.Apply("Gaming",Changes()));

  Assert(e.History().Single().Status=="restored");

 });

 Test("empty batch cannot create a session",() => { var (s,e,_) = Fixture(); Throws<InvalidOperationException>(()=>e.Apply("Gaming",[])); Assert(e.History().Count==0); });

 Test("legacy active journal migrates to typed versioned operations",() => {

  var json=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","legacy-active.json")); var j=JournalStore.Parse(json);

  Assert(j.SchemaVersion==2 && j.Operations[1].ValueType=="Guid" && j.IsActive);

  var (s,e,store)=Fixture(); s.Values["animations"]="Off"; s.Values["power"]="8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"; store.Save(j);

  Assert(e.Restore(store.Load().Single()).Count==0); Assert(s.Values["power"]=="381b4222-f694-41f0-9685-ff5bb260df2e");

 });

 Test("legacy partial restore preserves restored ownership",() => {

  var json=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","legacy-active.json")).Replace("\"Restored\": []","\"Restored\": [\"animations\"]");

  var j=JournalStore.Parse(json); Assert(j.Operations[0].State=="rolled back");

 });

 Test("journal path traversal and future schemas are rejected",() => {

  var j=new Journal { Id="../outside" }; Throws<InvalidDataException>(()=>JournalStore.Validate(j));

  j.Id=Guid.NewGuid().ToString("N"); j.SchemaVersion=99; Throws<InvalidDataException>(()=>JournalStore.Validate(j));

 });

 Test("malformed history is quarantined without hiding valid recovery",() => {

  var directory=Path.Combine(root,Guid.NewGuid().ToString("N")); var store=new JournalStore(directory); store.Save(new Journal { Status="restored" });

  File.WriteAllText(Path.Combine(directory,Guid.NewGuid().ToString("N")+".json"),"{broken");

  Assert(store.Load().Count==1); Assert(store.HasQuarantinedHistory); Assert(Directory.GetFiles(Path.Combine(directory,"quarantine")).Length==1);

  var s=new FakeSettings(); var e=new TuningEngine(s,store,new PermissiveCapabilities());

  Throws<InvalidOperationException>(()=>e.Apply("Gaming",Changes())); Assert(s.Writes==0);

 });

 Test("mismatched file ID is quarantined",() => {

  var directory=Path.Combine(root,Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);

  File.WriteAllText(Path.Combine(directory,Guid.NewGuid().ToString("N")+".json"),JsonSerializer.Serialize(new Journal()));

  var store=new JournalStore(directory); Assert(store.Load().Count==0 && store.HasQuarantinedHistory);

 });

 Test("disk full before intent prevents mutation",() => {

  var (s,_,store)=Fixture(); var failing=new FailingStore(store){ FailAt=1 };

  Throws<IOException>(()=>new TuningEngine(s,failing,new PermissiveCapabilities()).Apply("Gaming",Changes())); Assert(s.Writes==0);

 });

 Test("permission failure before intent prevents mutation",() => {

  var (s,_,store)=Fixture(); var failing=new FailingStore(store){ Denied=true,FailAt=1 };

  Throws<UnauthorizedAccessException>(()=>new TuningEngine(s,failing,new PermissiveCapabilities()).Apply("Gaming",Changes())); Assert(s.Writes==0);

 });

 Test("interrupted save after mutation preserves recoverable originals",() => {

  var (s,_,store)=Fixture(); var failing=new FailingStore(store){ FailAt=3 };

  var e=new TuningEngine(s,failing,new PermissiveCapabilities()); Throws<InvalidOperationException>(()=>e.Apply("Gaming",Changes()));

  Assert(s.Values["animations"]=="On"); Assert(store.Load().Single().Changes[0].Before=="On");

 });

 Test("rollback denial retains unresolved record",() => {

  var (s,e,_) = Fixture(); e.Apply("Gaming",Changes()); s.FailKey="menus"; Assert(e.Restore(e.History().Single()).Count==1);

  Assert(e.History().Single().IsActive); s.FailKey=null; Assert(e.Restore(e.History().Single()).Count==0);

 });

 Test("mid-batch stale setting is never written",() => {

  var (s,e,_) = Fixture(); s.AfterWrite=()=>s.Values["menus"]="External";

  Throws<InvalidOperationException>(()=>e.Apply("Gaming",Changes())); Assert(s.Values["menus"]=="External"); Assert(s.Values["animations"]=="On");

 });

 Test("mid-batch policy change stops writes and records recovery denial",() => {

  var (s,_,store)=Fixture(); var policy=new FakeCapabilities(); s.AfterWrite=()=>policy.Denied=true;

  var e=new TuningEngine(s,store,policy); Throws<InvalidOperationException>(()=>e.Apply("Gaming",Changes()));

  Assert(s.Values["menus"]=="On" && store.Load().Single().IsActive); policy.Denied=false; Assert(e.Restore(store.Load().Single()).Count==0);

 });

 Test("cancellation at a safe boundary compensates completed work",() => {

  var (s,e,_) = Fixture(); using var cancel=new CancellationTokenSource(); s.AfterWrite=()=>cancel.Cancel();

  var result=e.ApplyAsync("Coding",Changes(),cancel.Token).GetAwaiter().GetResult();

  Assert(!result.Success && s.Values["animations"]=="On" && s.Values["menus"]=="On");

 });

 Test("unsupported capability blocks planning",() => {

  var s=new FakeSettings(); var p=new ExecutionPlanner(s,new FakeCapabilities { Denied=true });

  Throws<InvalidOperationException>(()=>p.Preview(new Dictionary<string,string>{{"animations","Off"}})); Assert(s.Writes==0);

 });

 Test("duplicate operations and unknown IDs cannot become commands",() => {

  var (s,e,_) = Fixture(); Throws<InvalidDataException>(()=>e.Apply("Gaming",[Changes()[0],Changes()[0]]));

  Throws<InvalidDataException>(()=>e.Apply("Gaming",[new("powershell","Unknown","On","Off")])); Assert(s.Writes==0);

 });

 Test("malformed profiles and unsupported versions are rejected",() => {

  Throws<InvalidDataException>(()=>ProfileStore.Parse("""{"SchemaVersion":9,"Name":"Bad","Parameters":[]}"""));

  Throws<InvalidDataException>(()=>ProfileStore.Serialize(new(1,"Bad",[new("dns-script",1,"anything")])));

  Throws<InvalidDataException>(()=>ProfileStore.Serialize(new(1,"Bad",[new("menus",99,"On")])));

  Throws<InvalidDataException>(()=>ProfileStore.Serialize(new(1,"Bad",[new("menus",1,"C:\\tool.exe")])));

 });

 Test("profiles exclude uninstall, DNS mutations and scripts",() => {

  foreach(var id in new[]{"uninstall","dns","script","service","registry"})

   Throws<InvalidDataException>(()=>ProfileStore.Serialize(new(1,"Blocked",[new(id,1,"On")])));

 });

 Test("custom profile and persistent queue preserve desired values only",() => {

  var p=new ProfileDocument(1,"My setup",[new("animations",1,"Off")]); var parsed=ProfileStore.Parse(ProfileStore.Serialize(p)); Assert(parsed.Parameters.Single().Value=="Off");

  var queue=new QueueStore(Path.Combine(root,"queue.json")); queue.Save(new Dictionary<string,string>{{"menus","Off"}}); Assert(queue.Load()["menus"]=="Off");

 });

 Test("update metadata rejects hostile hosts and malformed records",() => {

  var json="""[{"draft":false,"prerelease":true,"tag_name":"v9.0.0","html_url":"https://evil.example/eyad1dk/optimizer/releases/tag/v9"}]""";

  Assert(UpdateService.Parse(json,true,new(0,2,0)).ReleasePage==null);

  Assert(UpdateService.Parse("{",true,new(0,2,0)).Status=="Invalid");

  Assert(!UpdateService.IsOfficialPage(new("http://github.com/eyad1dk/optimizer/releases/tag/v3")));

 });

 Test("stable and preview update channels are separate",() => {

  var json="""[{"draft":false,"prerelease":true,"tag_name":"v0.3.0","html_url":"https://github.com/eyad1dk/optimizer/releases/tag/v0.3.0"}]""";

  Assert(UpdateService.Parse(json,false,new(0,2,0)).Status=="No releases");

  Assert(UpdateService.Parse(json,true,new(0,2,0)).Status=="Available");

  Throws<NotSupportedException>(UpdateService.InstallDownloadedArtifact);

 });

 Test("atomic orphan file cannot replace a completed journal",() => {

  var directory=Path.Combine(root,Guid.NewGuid().ToString("N")); var store=new JournalStore(directory);

  var j=new Journal { Status="restored" }; store.Save(j); File.WriteAllText(Path.Combine(directory,"orphan.tmp"),"{broken");

  Assert(store.Load().Single().Id==j.Id);

 });

 Test("restore-point skipped and failure outcomes never claim success",()=>{

  Assert(RestorePointService.ResultFromCode(0).State=="Created");Assert(RestorePointService.ResultFromCode(2).State=="Skipped");Assert(RestorePointService.ResultFromCode(3).State=="Unavailable");Assert(RestorePointService.ResultFromCode(4).State=="Denied");Assert(RestorePointService.ResultFromCode(123).State=="Failed");

 });

 Test("network target rejects URLs, credentials and script input",()=>{

  foreach(var value in new[]{"https://example.com/path","user@example.com","powershell; evil","","C:\\test.exe"})Throws<InvalidDataException>(()=>NetworkDiagnostics.ValidateEndpoint(value));

  Assert(NetworkDiagnostics.ValidateEndpoint("example.com")=="example.com");Assert(NetworkDiagnostics.ValidateEndpoint("::1")=="::1");

 });

 Test("default report excludes user paths, identities, commands and original values",()=>{

  var journal=new Journal{Profile="PRIVATE-PROFILE",Changes=[new("power","secret","PRIVATE-ORIGINAL","PRIVATE-TARGET")]};

  var text=ReportService.Serialize(null,null,[journal]);foreach(var secret in new[]{"PRIVATE-PROFILE","PRIVATE-ORIGINAL","PRIVATE-TARGET","Username","Network","CommandLine"})Assert(!text.Contains(secret));Assert(text.Contains("SchemaVersion"));

 });

 Test("local error logs redact messages and retain correlation",()=>{

  var directory=Path.Combine(root,"logs");var id=new LocalLogger(directory).Error(new IOException("C:\\Users\\Private\\SECRET-KEY"));var text=File.ReadAllText(Path.Combine(directory,"events.jsonl"));Assert(text.Contains(id)&&!text.Contains("SECRET-KEY"));

 });

 Test("official update links reject alternate ports and credentials",()=>{

  Assert(!UpdateService.IsOfficialPage(new("https://github.com:444/eyad1dk/optimizer/releases/tag/v9")));Assert(!UpdateService.IsOfficialPage(new("https://user@github.com/eyad1dk/optimizer/releases/tag/v9")));

 });

 Test("offline and rate-limited checks leave manual recovery usable",()=>{

  var client=new System.Net.Http.HttpClient(new StubHandler(_=>throw new System.Net.Http.HttpRequestException()));Assert(new UpdateService(client).CheckAsync(false).GetAwaiter().GetResult().Status=="Offline");

  client=new(new StubHandler(_=>new(System.Net.HttpStatusCode.TooManyRequests)));Assert(new UpdateService(client).CheckAsync(false).GetAwaiter().GetResult().Status=="Rate limited");

 });

 Test("interrupted metadata cannot replace the application",()=>{

  var client=new System.Net.Http.HttpClient(new StubHandler(_=>throw new IOException("Interrupted stream")));Assert(new UpdateService(client).CheckAsync(false).GetAwaiter().GetResult().Status=="Interrupted");Throws<NotSupportedException>(UpdateService.InstallDownloadedArtifact);

 });

 Test("unsigned or bad signature cannot enter an installation path",()=>{

  // No installer exists until a trusted publisher signature and signed manifest are configured.

  Throws<NotSupportedException>(UpdateService.InstallDownloadedArtifact);

 });

 Test("rollback save failure keeps the returned journal consistent and retryable",()=>{

  var s=new FakeSettings();var inner=new JournalStore(Path.Combine(root,Guid.NewGuid().ToString("N")));var failing=new FailingStore(inner);var e=new TuningEngine(s,failing,new PermissiveCapabilities());var j=e.Apply("Coding",Changes());failing.FailAt=failing.Count+2;Assert(e.Restore(j).Count>0);JournalStore.Validate(j);Assert(inner.Load().Single().IsActive);Assert(e.Restore(j).Count==0);Assert(inner.Load().Single().Status=="restored");

 });

 Test("null custom-profile entries are rejected before planning",()=>Throws<InvalidDataException>(()=>ProfileStore.Parse("""{"SchemaVersion":1,"Name":"Bad","Parameters":[null]}""")));



 Test("all new preference values survive exact simulated undo",()=>{

  foreach(var spec in NativePreferences.Specifications){var (settings,engine,store)=Fixture();var before=spec.Numeric?spec.Minimum.ToString():"On";var after=spec.Numeric?spec.Maximum.ToString():"Off";settings.Values[spec.Id]=before;engine.Apply("Custom",[new(spec.Id,spec.Id,before,after)]);Assert(settings.Values[spec.Id]==after);Assert(engine.Restore(store.Load().Single()).Count==0);Assert(settings.Values[spec.Id]==before);}

 });

 Test("numeric values reject invalid, padded and out-of-range input",()=>{

  foreach(var spec in NativePreferences.Specifications.Where(p=>p.Numeric)){foreach(var value in new[]{"-1","+1","01"," 1","NaN","1.2",(spec.Maximum+1).ToString()})Throws<InvalidDataException>(()=>Catalog.ValidateTarget(spec.Id,value));}

 });

 Test("numeric undo preserves an external change",()=>{

  var (settings,engine,store)=Fixture();settings.Values["mouse-speed"]="10";engine.Apply("Custom",[new("mouse-speed","Pointer","10","12")]);settings.Values["mouse-speed"]="8";Assert(engine.Restore(store.Load().Single()).Count==1);Assert(settings.Values["mouse-speed"]=="8");

 });

 Test("numeric profile roundtrip is typed and lossless",()=>{

  var doc=new ProfileDocument(1,"Input",[new("menu-delay",1,"275"),new("keyboard-repeat",1,"20")]);var restored=ProfileStore.Parse(ProfileStore.Serialize(doc));Assert(restored.Parameters[0].Value=="275");Assert(Catalog.ValueType("menu-delay")=="Integer");

 });

 Test("native argument conventions remain operation-specific",()=>{

  Assert(NativePreferences.Get("mouse-speed").Parameter==NativeParameter.PointerValue);Assert(NativePreferences.Get("keyboard-repeat").Parameter==NativeParameter.UiValue);Assert(NativePreferences.Get("minimize-animation").Parameter==NativeParameter.AnimationStructure);

 });

 string Csv(string value)=>"FrameTimeMs\n"+string.Join("\n",Enumerable.Repeat(value,100));

 Test("frame summary computes constant frame times",()=>{var result=FrameBenchmark.Parse(Csv("10"));Assert(result.Frames==100&&result.AverageFps==100&&result.LowOnePercent==100&&result.P99==10);});

 Test("frame summary uses slowest one percent for lows",()=>{var result=FrameBenchmark.Parse("FrameTimeMs\n"+string.Join("\n",Enumerable.Repeat("10",99).Append("100")));Assert(result.LowOnePercent==10&&result.P99==10);});

 Test("frame import rejects invalid numeric data",()=>{foreach(var value in new[]{"NaN","Infinity","0","-1","60001","oops"})Throws<InvalidDataException>(()=>FrameBenchmark.Parse(Csv(value)));});

 Test("frame import rejects ambiguous headers and short runs",()=>{Throws<InvalidDataException>(()=>FrameBenchmark.Parse("FrameTimeMs,MsBetweenPresents\n10,10"));Throws<InvalidDataException>(()=>FrameBenchmark.Parse("FrameTimeMs\n10"));});

 Test("frame import rejects mixed processes",()=>{Throws<InvalidDataException>(()=>FrameBenchmark.Parse("ProcessID,MsBetweenPresents\n1,10\n2,10"));});

 Test("frame import accepts quoted fields and rejects malformed quotes",()=>{Assert(FrameBenchmark.Parse("\"FrameTimeMs\"\n"+string.Join("\n",Enumerable.Repeat("\"10\"",30))).Frames==30);Throws<InvalidDataException>(()=>FrameBenchmark.Parse("FrameTimeMs\n\"10"));});

 Test("workspace inspection leaves project files intact",()=>{var folder=Path.Combine(root,"workspace");Directory.CreateDirectory(Path.Combine(folder,"node_modules"));var file=Path.Combine(folder,"node_modules","sample.txt");File.WriteAllText(file,"keep me");var result=WorkspaceInspector.Inspect(folder);Assert(result.Contains("No files were changed")&&File.ReadAllText(file)=="keep me");});


 Test("processor settings retain scheme identity and exact originals",()=>{
  var (settings,engine,store)=Fixture();foreach(var id in ProcessorPower.Ids){var before="381b4222-f694-41f0-9685-ff5bb260df2e|"+(id.Contains("boost")?"1":"25");var after="381b4222-f694-41f0-9685-ff5bb260df2e|"+(id.Contains("boost")?"2":"75");settings.Values[id]=before;var journal=engine.Apply("CPU",[new(id,id,before,after)]);Assert(engine.Restore(journal).Count==0&&settings.Values[id]==before);}
 });
 Test("processor values reject malformed schemes and percentages",()=>{foreach(var value in new[]{"25","oops|25","381b4222-f694-41f0-9685-ff5bb260df2e|101","381b4222-f694-41f0-9685-ff5bb260df2e|01"})Throws<InvalidDataException>(()=>ProcessorPower.Parse(value));});
 Test("processor settings cannot be batched with a plan switch",()=>{var (settings,_,_)=Fixture();settings.Values["power"]="381b4222-f694-41f0-9685-ff5bb260df2e";settings.Values["cpu-min-ac"]="381b4222-f694-41f0-9685-ff5bb260df2e|5";Throws<InvalidOperationException>(()=>new ExecutionPlanner(settings,new PermissiveCapabilities()).Preview(new Dictionary<string,string>{{"power","a1841308-3541-4fab-bc81-f71556f20b4a"},{"cpu-min-ac","381b4222-f694-41f0-9685-ff5bb260df2e|10"}}));});
 Test("optional services preserve exact simulated startup configuration",()=>{foreach(var name in ServicePreferences.Names){var (settings,engine,_)=Fixture();var id="service:"+name;settings.Values[id]="2:1";var journal=engine.Apply("Optional service",[new(id,id,"2:1","4:1")]);Assert(engine.Restore(journal).Count==0&&settings.Values[id]=="2:1");}});
 Test("service and system-command allowlists reject arbitrary execution",()=>{Assert(!ServicePreferences.Contains("service:wuauserv")&&!ServicePreferences.Contains("service:WinDefend"));Throws<InvalidDataException>(()=>ServicePreferences.Parse("0:0"));Throws<InvalidDataException>(()=>SystemCommands.Get("cmd.exe /c arbitrary"));});
 Test("selected undo retains other active operations",()=>{var (settings,engine,_)=Fixture();var journal=engine.Apply("Gaming",Changes());Assert(engine.RestoreSelectedAsync(journal,new HashSet<string>{"menus"}).GetAwaiter().GetResult().Count==0);Assert(settings.Values["menus"]=="On"&&settings.Values["animations"]=="Off"&&journal.IsActive);Assert(engine.Restore(journal).Count==0&&!journal.IsActive);});
 Test("selected undo rejects foreign IDs without writes",()=>{var (settings,engine,_)=Fixture();var journal=engine.Apply("Gaming",Changes());var writes=settings.Writes;Throws<InvalidDataException>(()=>engine.RestoreSelectedAsync(journal,new HashSet<string>{"unknown"}).GetAwaiter().GetResult());Assert(settings.Writes==writes);});
 Test("startup capture is durable before disabling and restart restores exact type",()=>{var folder=Path.Combine(root,"startup-one");var registry=new FakeStartupRegistry();var entry=new StartupRegistration("Example","%LOCALAPPDATA%\\Example.exe",Microsoft.Win32.RegistryValueKind.ExpandString,"present");registry.Items.Add(entry);registry.BeforeDelete=()=>Assert(Directory.GetFiles(folder,"*.json").Length==1);var manager=new StartupManager(folder,registry);manager.Disable(entry);Assert(registry.Items.Count==0);var restarted=new StartupManager(folder,registry);restarted.Restore(restarted.History().Single().Id);Assert(registry.Items.Single().Value==entry.Value&&registry.Items.Single().Kind==entry.Kind);});
 Test("startup interrupted deletion remains recoverable",()=>{var folder=Path.Combine(root,"startup-interrupted");var registry=new FakeStartupRegistry{ThrowAfterDelete=true};var entry=new StartupRegistration("Example","example.exe",Microsoft.Win32.RegistryValueKind.String,"present");registry.Items.Add(entry);var manager=new StartupManager(folder,registry);Throws<IOException>(()=>manager.Disable(entry));registry.ThrowAfterDelete=false;manager.Restore(manager.History().Single().Id);Assert(registry.Items.Single().Value=="example.exe");});
 Test("startup undo preserves external registrations",()=>{var registry=new FakeStartupRegistry();var entry=new StartupRegistration("Example","original.exe",Microsoft.Win32.RegistryValueKind.String,"present");registry.Items.Add(entry);var manager=new StartupManager(Path.Combine(root,"startup-conflict"),registry);manager.Disable(entry);registry.Items.Add(entry with{Value="external.exe"});Throws<InvalidOperationException>(()=>manager.Restore(manager.History().Single().Id));Assert(registry.Items.Single().Value=="external.exe");});
 Test("startup recovery rejects unsupported schema and unsafe identity",()=>{Throws<InvalidDataException>(()=>StartupManager.Validate(new(1,"../escape","Example","example.exe",Microsoft.Win32.RegistryValueKind.String,"disabled")));Throws<InvalidDataException>(()=>StartupManager.Validate(new(2,Guid.NewGuid().ToString("N"),"Example","example.exe",Microsoft.Win32.RegistryValueKind.String,"disabled")));});

 Test("game library preserves profile and opt-in consent",()=>{var document=new GameDocument(1,[new(Guid.NewGuid().ToString("N"),"Game",@"C:\Games\game.exe","Gaming",true)]);var parsed=GameLibrary.Parse(JsonSerializer.Serialize(document));Assert(parsed.Games.Single().Automatic&&parsed.Games.Single().Profile=="Gaming");});
 Test("game library rejects script paths and unsupported profiles",()=>{foreach(var game in new[]{new SavedGame(Guid.NewGuid().ToString("N"),"Game",@"C:\Games\script.ps1","Gaming"),new(Guid.NewGuid().ToString("N"),"Game",@"C:\Games\game.exe","arbitrary script")})Throws<InvalidDataException>(()=>GameLibrary.Parse(JsonSerializer.Serialize(new GameDocument(1,[game]))));});
 Test("process priority capture survives restart and restores exactly",()=>{var backend=new FakePriorityBackend();var directory=Path.Combine(root,"priority-one");var manager=new ProcessPriorityManager(directory,backend);manager.Apply(new(5,100,"Game",0),"AboveNormal");Assert(backend.Value=="AboveNormal");new ProcessPriorityManager(directory,backend).RestoreAll();Assert(backend.Value=="Normal");});
 Test("process priority undo preserves external changes and ended identities",()=>{var backend=new FakePriorityBackend();var manager=new ProcessPriorityManager(Path.Combine(root,"priority-two"),backend);manager.Apply(new(5,100,"Game",0),"AboveNormal");backend.Value="BelowNormal";Throws<InvalidOperationException>(manager.RestoreAll);Assert(backend.Value=="BelowNormal");backend.Value=null;manager.RestoreAll();Assert(manager.History().Single().State=="ended");});
 Test("priority controls reject realtime and arbitrary values",()=>{var manager=new ProcessPriorityManager(Path.Combine(root,"priority-invalid"),new FakePriorityBackend());Throws<InvalidDataException>(()=>manager.Apply(new(5,100,"Game",0),"RealTime"));});
 Test("GPU activity groups process counters by engine without adding independent engines",()=>{Assert(GpuActivityProbe.Aggregate([("pid_1_luid_A_eng_0",20),("pid_2_luid_A_eng_0",30),("pid_3_luid_A_eng_1",40)])==50);Assert(GpuActivityProbe.Aggregate([("invalid",50)])==null);Assert(GpuActivityProbe.Aggregate([("pid_1_luid_A_eng_0",double.NaN)])==null);});
 Test("malformed game library is preserved before any save",()=>{var path=Path.Combine(root,"malformed-games.json");File.WriteAllText(path,"broken");Throws<JsonException>(()=>new GameLibrary(path).Save([]));Assert(File.ReadAllText(path)=="broken");});
 Test("pointer acceleration keeps both thresholds and rejects malformed native states",()=>{Assert(PointerAcceleration.Choices("6,10,1").Select(c=>c.Value).SequenceEqual(new[]{"6,10,0","6,10,1","6,10,2"}));foreach(var value in new[]{"6,10,3","-1,10,0","06,10,0","6,10","6,10,0,1"})Throws<InvalidDataException>(()=>PointerAcceleration.Validate(value));});
 Test("repair commands use fixed arguments and media-aware optimization",()=>{Assert(SystemCommands.Get("dism-repair").Arguments.Contains("/RestoreHealth"));Assert(SystemCommands.Get("sfc-repair").Arguments.SequenceEqual(new[]{"/scannow"}));Assert(SystemCommands.Get("drive-optimize").Arguments.Contains("/O"));Throws<InvalidDataException>(()=>SystemCommands.Get("powershell.exe"));});
 Test("all native operations expose complete audit contracts in requested categories",()=>{Assert(OptimizationAudit.All.Select(a=>a.Id).Distinct().Count()==Catalog.Operations.Length+2);foreach(var operation in Catalog.Operations){var audit=OptimizationAudit.For(operation);Assert(OptimizationAudit.Categories.Contains(audit.Category));Assert(new[]{audit.Method,audit.Windows10,audit.Windows11,audit.Detect,audit.Apply,audit.Verify,audit.Restore,audit.Disadvantages,audit.Administrator,audit.Restart}.All(x=>!string.IsNullOrWhiteSpace(x)));}});
 Test("CPU paired increases apply max before min and reverse safely",()=>{var scheme="381b4222-f694-41f0-9685-ff5bb260df2e";var backend=new BoundarySettings(scheme,10,50);var engine=new TuningEngine(backend,new JournalStore(Path.Combine(root,"cpu-pair-up")),new PermissiveCapabilities());var journal=engine.Apply("Manual",[new("cpu-min-ac","minimum",scheme+"|10",scheme+"|70"),new("cpu-max-ac","maximum",scheme+"|50",scheme+"|90")]);Assert(journal.Operations[0].Id=="cpu-max-ac");Assert(engine.Restore(journal).Count==0&&backend.Min==10&&backend.Max==50);});
 Test("CPU paired decreases apply min before max regardless of queue order",()=>{var scheme="381b4222-f694-41f0-9685-ff5bb260df2e";var backend=new BoundarySettings(scheme,60,90);var engine=new TuningEngine(backend,new JournalStore(Path.Combine(root,"cpu-pair-down")),new PermissiveCapabilities());var journal=engine.Apply("Manual",[new("cpu-max-ac","maximum",scheme+"|90",scheme+"|40"),new("cpu-min-ac","minimum",scheme+"|60",scheme+"|20")]);Assert(journal.Operations[0].Id=="cpu-min-ac");Assert(engine.Restore(journal).Count==0&&backend.Min==60&&backend.Max==90);});
 Test("invalid CPU pair and mixed plan switch are rejected before any write",()=>{var scheme="381b4222-f694-41f0-9685-ff5bb260df2e";Throws<InvalidDataException>(()=>OperationOrdering.Order([new("cpu-min-ac","min",scheme+"|10",scheme+"|90"),new("cpu-max-ac","max",scheme+"|50",scheme+"|40")]));var (settings,engine,_)=Fixture();Throws<InvalidOperationException>(()=>engine.Apply("Bad",[new("power","plan",scheme,scheme),new("cpu-min-ac","min",scheme+"|10",scheme+"|20")]));Assert(settings.Writes==0);});
 Test("recovery uses restore policy without bypassing actual restore denial",()=>{var settings=new FakeSettings();var policy=new SplitRecoveryCapabilities();var engine=new TuningEngine(settings,new JournalStore(Path.Combine(root,"restore-policy")),policy);var journal=engine.Apply("Test",Changes());policy.DenyApply=true;Assert(engine.Restore(journal).Count==0&&settings.Values["animations"]=="On");Assert(policy.RestoreCalls>0);});
 Test("startup manager detects a backend that silently ignores disable",()=>{var registry=new SilentStartupRegistry();var manager=new StartupManager(Path.Combine(root,"startup-silent"),registry);Throws<IOException>(()=>manager.Disable(registry.Entries().Single()));Assert(manager.History().Single().State=="prepared");manager.Restore(manager.History().Single().Id);Assert(manager.History().Single().State=="restored");});
 Test("mouse acceleration journal restores the complete original tuple",()=>{var (settings,engine,store)=Fixture();settings.Values[PointerAcceleration.Id]="6,10,1";engine.Apply("Pointer",[new(PointerAcceleration.Id,"Pointer","6,10,1","6,10,0")]);Assert(engine.Restore(store.Load().Single()).Count==0&&settings.Values[PointerAcceleration.Id]=="6,10,1");});
 Test("startup manager rejects silent restoration and retains its original",()=>{var registry=new FakeStartupRegistry();registry.Items.Add(new("Example","example.exe",Microsoft.Win32.RegistryValueKind.String,"present"));var manager=new StartupManager(Path.Combine(root,"silent-startup-restore"),registry);manager.Disable(registry.Entries().Single());registry.IgnoreRestore=true;Throws<IOException>(()=>manager.Restore(manager.History().Single().Id));Assert(manager.History().Single().State=="disabled");registry.IgnoreRestore=false;manager.Restore(manager.History().Single().Id);Assert(registry.Items.Single().Value=="example.exe");});
 Test("noncanonical power targets cannot cause a write and false verification failure",()=>{var uppercase="381B4222-F694-41F0-9685-FF5BB260DF2E";Throws<InvalidDataException>(()=>Catalog.ValidateTarget("power",uppercase));Throws<InvalidDataException>(()=>ProcessorPower.Parse(uppercase+"|50"));});
 Test("older journals with uppercase GUIDs remain recoverable",()=>{var before="381B4222-F694-41F0-9685-FF5BB260DF2E";var after="A1841308-3541-4FAB-BC81-F71556F20B4A";var json=JsonSerializer.Serialize(new Journal{Status="applied",Changes=[new("power","Plan",before,after)],Operations=[new(){Id="power",ValueType="Guid",Before=before,Target=after,State="applied"}]});var journal=JournalStore.Parse(json);Assert(journal.Changes.Single().Before==before.ToLowerInvariant()&&journal.Operations.Single().Target==after.ToLowerInvariant());});

 Test("V2 DNS action is a fixed command with no arbitrary arguments",()=>{var task=SystemCommands.Get("dns-flush");Assert(task.Executable=="ipconfig.exe"&&task.Arguments.SequenceEqual(new[]{"/flushdns"})&&task.Administrator);Throws<InvalidDataException>(()=>SystemCommands.Get("dns-flush & whoami"));});
 Test("cleanup helper exposes eight fixed roots",()=>{Assert(CacheCatalog.All.Length==8);Assert(CacheCatalog.Get("prefetch").Pattern=="*.pf"&&!CacheCatalog.Get("prefetch").Recursive);Throws<InvalidDataException>(()=>CacheCatalog.Get("C:\\"));});
 Test("native V2 cleanup removes old fixture and preserves recent file",()=>{var folder=Path.Combine(root,"v2-delete");Directory.CreateDirectory(folder);var old=Path.Combine(folder,"old.tmp");File.WriteAllText(old,"12345678");File.SetLastWriteTimeUtc(old,DateTime.UtcNow.AddDays(-10));var fresh=Path.Combine(folder,"fresh.tmp");File.WriteAllText(fresh,"keep");var cleaner=new CacheCleaner(new("fixture","fixture",folder,"*",7,true,false,""));var scan=cleaner.Execute(false);Assert(scan.FilesBefore==1&&File.Exists(old));var result=cleaner.Execute(true);Assert(result.FilesRemoved==1&&result.BytesRemoved==8&&result.FilesAfter==0&&!File.Exists(old)&&File.Exists(fresh));});
 Test("locked cleanup fixture is reported rather than claimed removed",()=>{var folder=Path.Combine(root,"v2-locked");Directory.CreateDirectory(folder);var file=Path.Combine(folder,"locked.pf");File.WriteAllText(file,"keep");var cleaner=new CacheCleaner(new("fixture","fixture",folder,"*.pf",0,false,false,""));using(var locked=new FileStream(file,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){var result=cleaner.Execute(true);Assert(result.FilesRemoved==0&&result.FilesSkipped>0&&!result.Success);}Assert(File.Exists(file));});
 Test("Prefetch filtering preserves unrelated files and subdirectories",()=>{var folder=Path.Combine(root,"v2-prefetch");Directory.CreateDirectory(Path.Combine(folder,"ReadyBoot"));File.WriteAllText(Path.Combine(folder,"APP.pf"),"remove");File.WriteAllText(Path.Combine(folder,"layout.ini"),"keep");File.WriteAllText(Path.Combine(folder,"ReadyBoot","child.pf"),"keep");var result=new CacheCleaner(new("fixture","fixture",folder,"*.pf",0,false,false,"")).Execute(true);Assert(result.FilesRemoved==1&&File.Exists(Path.Combine(folder,"layout.ini"))&&File.Exists(Path.Combine(folder,"ReadyBoot","child.pf")));});
 Test("changed cleanup fixture is preserved at delete boundary",()=>{var folder=Path.Combine(root,"v2-stale");Directory.CreateDirectory(folder);var file=Path.Combine(folder,"changed.tmp");File.WriteAllText(file,"before");var cleaner=new CacheCleaner(new("fixture","fixture",folder,"*",0,false,false,""));var item=cleaner.Scan().Files.Single();File.WriteAllText(file,"changed content");Throws<IOException>(()=>cleaner.DeleteVerified(item));Assert(File.Exists(file));});
 Test("cleanup refuses file outside verified root",()=>{var folder=Path.Combine(root,"v2-boundary");Directory.CreateDirectory(folder);var file=Path.Combine(root,"outside.tmp");File.WriteAllText(file,"keep");var cleaner=new CacheCleaner(new("fixture","fixture",folder,"*",0,false,false,""));Throws<InvalidDataException>(()=>cleaner.DeleteVerified(new(file,4,File.GetLastWriteTimeUtc(file).ToFileTimeUtc())));Assert(File.Exists(file));});
 Test("independent direct settings keep separately recoverable originals",()=>{var(s,e,store)=Fixture();var a=e.ApplyAsync("A",[new("animations","Animations","On","Off")],allowIndependent:true).GetAwaiter().GetResult();var b=e.ApplyAsync("B",[new("menus","Menus","On","Off")],allowIndependent:true).GetAwaiter().GetResult();Assert(a.Success&&b.Success&&store.Load().Count==2);e.Restore(b.Journal);Assert(s.Values["animations"]=="Off"&&s.Values["menus"]=="On");e.Restore(a.Journal);Assert(s.Values["animations"]=="On");});
 Test("direct settings reject overlapping originals",()=>{var(s,e,store)=Fixture();e.ApplyAsync("A",[new("animations","Animations","On","Off")],allowIndependent:true).GetAwaiter().GetResult();Throws<InvalidOperationException>(()=>e.ApplyAsync("B",[new("animations","Animations","Off","On")],allowIndependent:true).GetAwaiter().GetResult());Assert(s.Values["animations"]=="Off");});

 Test("binary action derives from current state, not dropdown target",()=>{var item=new OperationItem(Catalog.Get("menus"));item.Target="On";item.Detect("On");Assert(item.IsBinary&&!item.HasValueSelector&&item.ActionLabel=="Disable");item.Detect("Off");Assert(item.ActionLabel=="Enable");Assert(item.BinaryTarget("On")=="Off");});
 Test("numeric settings retain selectors",()=>{var item=new OperationItem(Catalog.Get("menu-delay"));item.Detect("400");Assert(!item.IsBinary&&item.HasValueSelector&&item.ActionLabel=="Apply");});
 Test("hardware identities and DNS reject command text",()=>{foreach(var id in new[]{"hw:policy:defender","hw:service:WinDefend","hw:net:dns:../bad","hw:compression:extra"})Throws<InvalidDataException>(()=>HardwareBackend.ValidateId(id));Assert(HardwareBackend.CustomDns("1.1.1.1, 8.8.8.8")=="static:1.1.1.1,8.8.8.8");foreach(var value in new[]{"1.1.1.1;whoami","0.0.0.0","1.1.1.1,1.1.1.1","127.1","8.8.8.8 $(cmd)"})Throws<InvalidDataException>(()=>HardwareBackend.CustomDns(value));});
 Test("documented policy absence survives apply and exact restore",()=>{var(s,e,_)=Fixture();s.Values["hw:policy:ads"]="absent";var j=e.Apply("Privacy",[new("hw:policy:ads","Advertising permission","absent","1")]);Assert(s.Values["hw:policy:ads"]=="1");Assert(e.Restore(j).Count==0&&s.Values["hw:policy:ads"]=="absent");});
 Test("dynamic DNS history preserves automatic rather than DHCP address snapshot",()=>{var(s,e,_)=Fixture();var id="hw:net:dns:952c94bd-0c29-4d04-a65a-dec1d0d97e91";s.Values[id]="dhcp";var j=e.Apply("DNS",[new(id,"DNS","dhcp","static:1.1.1.1")]);Assert(e.Restore(j).Count==0&&s.Values[id]=="dhcp");});
 Test("dynamic property restore preserves external modifications",()=>{var(s,e,_)=Fixture();var id="hw:net:prop:952c94bd-0c29-4d04-a65a-dec1d0d97e91:KkVFRQ==";s.Values[id]="1";var j=e.Apply("Driver",[new(id,"EEE","1","0")]);s.Values[id]="2";Assert(e.Restore(j).Count==1&&s.Values[id]=="2");});
 Test("silent adaptive write fails verification and keeps original",()=>{var(s,e,_)=Fixture();s.Values["hw:compression"]="Off";s.IgnoreWrites=true;var result=e.ApplyAsync("Compression",[new("hw:compression","Compression","Off","On")]).GetAwaiter().GetResult();Assert(!result.Success&&s.Values["hw:compression"]=="Off");});
 Test("CPU boost has a bounded documented index and scheme identity",()=>{var scheme="381b4222-f694-41f0-9685-ff5bb260df2e";Catalog.ValidateTarget("cpu-boost-ac",scheme+"|2");Throws<InvalidDataException>(()=>Catalog.ValidateTarget("cpu-boost-ac",scheme+"|100"));Assert(ProcessorPower.SettingId("cpu-boost-dc").ToString()=="be337238-0d82-4146-a960-4f3749d470c7");Assert(ProcessorPower.Choices("cpu-boost-ac",scheme+"|2").Count()==5);});
 JsonElement J(string json)=>JsonDocument.Parse(json).RootElement.Clone();
 var guid="952c94bd-0c29-4d04-a65a-dec1d0d97e91";var rss="hw:net:rss:"+guid;
 HardwareSnapshot Machine(bool? laptop=false,string medium="802.3",int build=19045,int threads=6,long ram=16L*1024*1024*1024)=>new(build,"Fixture",laptop,threads,ram,ram/2,J("[]"),J("[{\"Name\":\"GPU\"}]"),J("{\"Guid\":\""+guid+"\",\"Hardware\":true,\"Medium\":\""+medium+"\"}"),J("[{\"MediaType\":\"SSD\"}]"),J("[]"),J("[]"),J("[]"),100,[],[]);
 var states=new Dictionary<string,string>{{"hw:trim","On"},{"hw:compression","Off"},{"menus","On"},{"animations","On"},{"power","381b4222-f694-41f0-9685-ff5bb260df2e"},{rss,"Off"},{"hw:policy:dvr","1"}};var supported=states.Keys.ToHashSet();var plans=new[]{"381b4222-f694-41f0-9685-ff5bb260df2e","8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c","a1841308-3541-4fab-bc81-f71556f20b4a"}.ToHashSet();
 IReadOnlyList<Recommendation> Rules(HardwareSnapshot h,string focus="Balanced",bool? battery=false,bool noRecording=false)=>RecommendationEngine.Calculate(h,states,supported,plans,battery,focus,noRecording);
 Test("recommendations recognize already optimized SSD TRIM and deduplicate IDs",()=>{var r=Rules(Machine());Assert(r.Single(x=>x.Id=="hw:trim").Optimized);Assert(r.Select(x=>x.Id).Distinct().Count()==r.Count);});
 Test("HDD or unknown media does not get an SSD recommendation",()=>{Assert(!Rules(Machine() with{Disks=J("[{\"MediaType\":\"HDD\"}]")}).Any(r=>r.Id=="hw:trim"));});
 Test("Wi-Fi never receives Ethernet-only recommendations",()=>{Assert(!Rules(Machine(medium:"Native 802.11"),"Competitive").Any(r=>r.Category=="Network"));});
 Test("laptops and unknown power state do not get maximum performance",()=>{Assert(!Rules(Machine(laptop:true),"Gaming").Any(r=>r.Id=="power"));Assert(!Rules(Machine(),"Gaming",null).Any(r=>r.Id=="power"));Assert(!Rules(Machine(laptop:null),"Gaming").Any(r=>r.Id=="power"));});
 Test("custom power plans are preserved",()=>{var copy=new Dictionary<string,string>(states){["power"]="66666666-6666-6666-6666-666666666666"};Assert(!RecommendationEngine.Calculate(Machine(),copy,supported,plans,false,"Gaming",false).Any(r=>r.Id=="power"));});
 Test("compression recommendation requires pressure or limited RAM and sufficient CPU",()=>{Assert(!Rules(Machine()).Any(r=>r.Id=="hw:compression"));Assert(Rules(Machine(ram:4L*1024*1024*1024)).Any(r=>r.Id=="hw:compression"));Assert(!Rules(Machine(threads:2,ram:4L*1024*1024*1024)).Any(r=>r.Id=="hw:compression"));});
 Test("recording opt-out and Windows 10 support are both required",()=>{Assert(!Rules(Machine(),"Gaming").Any(r=>r.Id=="hw:policy:dvr"));Assert(Rules(Machine(),"Gaming",false,true).Any(r=>r.Id=="hw:policy:dvr"));Assert(!Rules(Machine(build:22631),"Gaming",false,true).Any(r=>r.Id=="hw:policy:dvr"));});
 Test("unsupported capabilities never become recommendations",()=>{Assert(RecommendationEngine.Calculate(Machine(),states,new HashSet<string>(),plans,false,"Competitive",true).Count==0);});
 Test("unknown workload and older Windows receive no automatic changes",()=>{Assert(Rules(Machine(),"unknown").Count==0);Assert(Rules(Machine(build:17763)).Count==0);});


 Test("pagefile snapshot rejects ambiguous or incomplete recovery values",()=>{Catalog.ValidateTarget("hw:pagefile","{\"Automatic\":true,\"Files\":[]}");foreach(var value in new[]{"{ \"Automatic\":true,\"Files\":[]}","{\"Automatic\":false,\"Files\":[]}","{\"Automatic\":false,\"Files\":[{\"Name\":\"C:\\\\file.txt\",\"InitialSize\":1,\"MaximumSize\":2}]}"})Throws<InvalidDataException>(()=>Catalog.ValidateTarget("hw:pagefile",value));});
 Test("maintenance allowlist excludes arbitrary commands and security services",()=>{Throws<InvalidDataException>(()=>Maintenance.Validate("cmd","/c whoami"));Throws<InvalidDataException>(()=>Maintenance.Validate("renew","Ethernet;whoami"));Maintenance.Validate("dns-test","example.com");foreach(var service in new[]{"WinDefend","mpssvc","wuauserv","CryptSvc","Dhcp","EventLog"})Throws<InvalidDataException>(()=>HardwareBackend.ValidateId("hw:service:"+service));});
 Test("policy binary restore maps unconfigured to permission without losing original",()=>{var h=new HardwareControl("hw:policy:ads","Advertising permission","Privacy","absent",[new("0","Enabled"),new("1","Disabled"),new("absent","Not configured")],"",true,"0","1",true);var item=new OperationItem(HardwareBackend.Definition(h.Id)){Hardware=h};item.Detect("absent");Assert(item.ActionLabel=="Disable"&&item.BinaryTarget("absent")=="1");item.Detect("1");Assert(item.ActionLabel=="Enable"&&h.Effective("absent")=="0");});

 Console.WriteLine($"{passed} regression checks passed. No Windows settings were changed. Native deletion tests use only disposable fixtures created by the test suite.");

}

finally { Directory.Delete(root,true); }

sealed class FakeSettings : ISettings

{

 public Dictionary<string,string> Values=new(){{"animations","On"},{"menus","On"}};

 public string? FailKey; public bool WriteThenFail,IgnoreWrites; public int Writes; public Action? AfterWrite;

 public string Read(string key)=>Values[key];

 public void Write(string key,string value) { Writes++; if(key==FailKey)throw new IOException("Simulated rejection"); if(!IgnoreWrites)Values[key]=value; AfterWrite?.Invoke(); if(WriteThenFail)throw new IOException("Interrupted"); }

}

sealed class StubHandler(Func<System.Net.Http.HttpRequestMessage,System.Net.Http.HttpResponseMessage> response) : System.Net.Http.HttpMessageHandler

{ protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request,CancellationToken token)=>Task.FromResult(response(request)); }

sealed class FakeCapabilities : ICapabilityService

{ public bool Denied; public Capability Check(string id,string value)=>new(!Denied,Denied?"Managed restriction":"Eligible"); }

sealed class FailingStore(IJournalStore inner) : IJournalStore

{

 public int FailAt,Count; public bool Denied;

 public IReadOnlyList<string> Warnings=>inner.Warnings; public bool HasQuarantinedHistory=>inner.HasQuarantinedHistory;

 public List<Journal> Load()=>inner.Load();

 public void Save(Journal j) { if(++Count==FailAt){ if(Denied)throw new UnauthorizedAccessException("Denied"); throw new IOException("Disk full"); } inner.Save(j); }

}


sealed class FakeStartupRegistry : IStartupRegistry
{
 public List<StartupRegistration> Items=[];public Action? BeforeDelete;public bool ThrowAfterDelete,IgnoreRestore;
 public List<StartupRegistration> Entries()=>Items.ToList();
 public void DeleteIfUnchanged(StartupRegistration entry){BeforeDelete?.Invoke();var existing=Items.SingleOrDefault(x=>x.Name.Equals(entry.Name,StringComparison.OrdinalIgnoreCase));if(existing==null||existing.Value!=entry.Value||existing.Kind!=entry.Kind)throw new InvalidOperationException();Items.Remove(existing);if(ThrowAfterDelete)throw new IOException();}
 public void RestoreIfMissing(StartupRecovery record){if(IgnoreRestore)return;var existing=Items.SingleOrDefault(x=>x.Name.Equals(record.Name,StringComparison.OrdinalIgnoreCase));if(existing!=null){if(existing.Value!=record.Original||existing.Kind!=record.Kind)throw new InvalidOperationException();return;}Items.Add(new(record.Name,record.Original,record.Kind,"restored"));}
}

sealed class FakePriorityBackend : IProcessPriorityBackend
{
 public string? Value="Normal";public string? Read(int id,long ticks)=>id==5&&ticks==100?Value:null;
 public void Write(int id,long ticks,string expected,string value){if(Read(id,ticks)!=expected)throw new InvalidOperationException();Value=value;}
}

sealed class BoundarySettings(string scheme,uint minimum,uint maximum):ISettings
{
 public uint Min=minimum,Max=maximum;
 public string Read(string id)=>scheme+"|"+(id=="cpu-min-ac"?Min:Max);
 public void Write(string id,string value){var parsed=ProcessorPower.Parse(value);if(id=="cpu-min-ac"){if(parsed.Percent>Max)throw new IOException("Invalid minimum");Min=parsed.Percent;}else{if(parsed.Percent<Min)throw new IOException("Invalid maximum");Max=parsed.Percent;}}
}
sealed class SplitRecoveryCapabilities:ICapabilityService
{
 public bool DenyApply;public int RestoreCalls;
 public Capability Check(string id,string value)=>new(!DenyApply,"Selection guard");
 public Capability CheckRestore(string id,string value){RestoreCalls++;return new(true,"Restore allowed by Windows");}
}
sealed class SilentStartupRegistry:IStartupRegistry
{
 public List<StartupRegistration> Entries()=>[new("Example","example.exe",Microsoft.Win32.RegistryValueKind.String,"present")];
 public void DeleteIfUnchanged(StartupRegistration entry){}
 public void RestoreIfMissing(StartupRecovery recovery){}
}