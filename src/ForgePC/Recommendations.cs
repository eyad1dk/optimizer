using System.Text;
using System.Text.Json;
namespace ForgePC;

public record Recommendation(string Id,string Category,string Target,string Reason,bool Optimized);
public static class RecommendationEngine
{
 public static readonly string[] Focuses=["Balanced","Gaming","Competitive","Coding","Performance","Quiet","Battery Saver","Network Low Latency","Maximum Throughput","Low-End PC"];
 public static IReadOnlyList<Recommendation> Calculate(HardwareSnapshot h,IReadOnlyDictionary<string,string> states,IReadOnlySet<string> supported,IReadOnlySet<string> installedPlans,bool? onBattery,string focus,bool noRecording)
 {
  if(!Focuses.Contains(focus)||h.Build<19045)return [];
  var rules=new Dictionary<string,Recommendation>();
  void Add(string id,string target,string category,string reason){if(!supported.Contains(id)||!states.TryGetValue(id,out var current))return;var c=h.Controls.FirstOrDefault(c=>c.Id==id);if(c!=null&&!c.Choices.Any(x=>x.Value==target))return;rules[id]=new(id,category,target,reason,c==null?current==target:c.Effective(current)==c.Effective(target));}
  bool pressure=h.TotalMemory>0&&h.FreeMemory>=0&&(double)h.FreeMemory/h.TotalMemory<.2;
  bool lowMemory=h.TotalMemory>0&&h.TotalMemory<=8L*1024*1024*1024;
  if(h.Threads>=4&&(lowMemory||pressure))Add("hw:compression","On","Memory","Limited RAM or current memory pressure; compression can reduce paging at a CPU cost.");
  if(h.Controls.FirstOrDefault(c=>c.Id=="hw:pagefile") is {} page&&pressure){var automatic=page.Choices.FirstOrDefault(c=>c.Label=="System managed");if(automatic!=null)Add(page.Id,automatic.Value,"Memory","Current memory pressure: automatic pagefile sizing permits Windows to manage commit capacity; disk space and restart required.");}
  bool solid=h.Disks.ValueKind==JsonValueKind.Array&&h.Disks.EnumerateArray().Any(d=>d.TryGetProperty("MediaType",out var t)&&t.ToString()=="SSD");
  if(solid)Add("hw:trim","On","Storage","An SSD was detected. NTFS deletion notifications let a supporting storage stack reclaim deleted blocks.");
  if(focus is "Competitive"||focus=="Low-End PC"&&(lowMemory||pressure))foreach(var id in new[]{"animations","menus","minimize-animation"})Add(id,"Off","Windows","Reduced desktop motion was selected for this workload. This is a responsiveness preference, not an FPS claim.");
  const string balanced="381b4222-f694-41f0-9685-ff5bb260df2e",performance="8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",saver="a1841308-3541-4fab-bc81-f71556f20b4a";
  // Unknown/OEM/custom schemes are deliberately preserved.
  if(states.TryGetValue("power",out var currentPlan)&&new[]{balanced,performance,saver}.Contains(currentPlan)){
   string? target=focus is "Gaming" or "Competitive" or "Performance"&&h.Laptop==false&&onBattery==false&&h.Threads>=4?performance:focus=="Battery Saver"&&h.Laptop==true?saver:focus=="Balanced"||focus=="Quiet"?balanced:null;
   if(target!=null&&installedPlans.Contains(target))Add("power",target,"CPU",target==performance?"Desktop on AC with a standard power scheme; higher energy use and heat are possible.":"The selected workload favors a standard energy-balanced power policy.");
  }
  if(noRecording&&focus is "Gaming" or "Competitive"&&h.Build<22000&&h.Graphics.ValueKind==JsonValueKind.Array&&h.Graphics.GetArrayLength()>0)Add("hw:policy:dvr","0","Gaming","You opted out of recording. Windows 10 policy can block Game DVR recording; capture functionality is lost.");
  var n=h.Network;
  if(n.ValueKind==JsonValueKind.Object&&n.TryGetProperty("Hardware",out var hw)&&hw.ValueKind==JsonValueKind.True&&n.TryGetProperty("Guid",out var guid)){
   var g=guid.GetString();string medium=n.GetProperty("Medium").ToString();bool ethernet=medium=="802.3";
   if(h.Threads>=4&&ethernet)Add("hw:net:rss:"+g,"On","Network","Physical Ethernet and multiple CPU threads detected; RSS distributes receive processing. Reconfiguration may interrupt connectivity.");
   if(focus=="Maximum Throughput"&&h.Threads>=4&&ethernet)foreach(var kind in new[]{"rsc4","rsc6"})Add("hw:net:"+kind+":"+g,"On","Network","Throughput focus: supported coalescing can reduce receive CPU work; latency effects vary.");
   if(focus is "Network Low Latency" or "Competitive"&&ethernet&&h.Laptop==false&&onBattery==false&&h.Threads>=4)foreach(var keyword in new[]{"*InterruptModeration","*EEE"}){
    var id="hw:net:prop:"+g+":"+Convert.ToBase64String(Encoding.UTF8.GetBytes(keyword));var c=h.Controls.FirstOrDefault(c=>c.Id==id&&c.Binary);if(c!=null)Add(id,c.Off,"Network",keyword=="*EEE"?"Explicit latency focus on desktop Ethernet: disabling EEE avoids energy-saving transitions at a power cost; no measured latency gain claimed.":"Explicit latency focus: fewer batched interrupts may increase CPU load and reduce throughput. Test your workload; driver restart required.");
   }
  }
  return rules.Values.OrderBy(r=>r.Category).ThenBy(r=>r.Id).ToArray();
 }
}
