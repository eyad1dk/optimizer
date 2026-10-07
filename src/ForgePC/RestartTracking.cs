namespace ForgePC;

public enum RestartKind { None, Explorer, SignOut, Windows }
public static class RestartTracking
{
 public static RestartKind Required(string id)=>id is "hw:pagefile" or "hw:compression"||id.StartsWith("hw:net:prop:")?RestartKind.Windows:RestartKind.None;
 public static DateTime BootUtc=>DateTime.UtcNow-TimeSpan.FromMilliseconds(Environment.TickCount64);
 public static bool Pending(OperationRecord operation,DateTime bootUtc)=>operation.RestartNeeded!=RestartKind.None&&operation.LastWriteUtc is {} changed&&changed>bootUtc.AddSeconds(5);
 public static string Summary(IEnumerable<Journal> history,DateTime bootUtc)
 {
  var pending=history.SelectMany(j=>j.Operations).Where(o=>Pending(o,bootUtc)).Select(o=>o.RestartNeeded).Distinct().ToArray();
  return pending.Contains(RestartKind.Windows)?"Windows restart pending":pending.Contains(RestartKind.SignOut)?"Sign out pending":pending.Contains(RestartKind.Explorer)?"Explorer restart pending":"No restart flagged by EZoptimizer";
 }
}
