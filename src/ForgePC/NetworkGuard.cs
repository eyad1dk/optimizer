using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
namespace ForgePC;

// Capture before the durable write; check the same adapter after read-back.
// This proves local connectivity only, not Internet reachability or DNS quality.
public interface IChangeGuardedSettings { Action? PrepareConnectivityCheck(string id); }
public record LinkState(bool Present,bool Up,bool HasAddress,bool HasGateway)
{
 public bool Connected=>Present&&Up&&HasAddress&&HasGateway;
}
public static class NetworkGuard
{
 public static Action? Prepare(string id,Func<Guid,LinkState>? probe=null,Action? wait=null)
 {
  if(!id.StartsWith("hw:net:"))return null;
  HardwareBackend.ValidateId(id);var guid=Guid.Parse(id.Split(':')[3]);
  probe??=Read;wait??=()=>Thread.Sleep(500);
  if(!probe(guid).Connected)throw new IOException("Network change blocked: this adapter must be connected with an IPv4 address and gateway before tuning.");
  return ()=>{
   for(int attempt=0;attempt<6;attempt++){if(probe(guid).Connected)return;if(attempt<5)wait();}
   throw new IOException("The adapter lost local IPv4 connectivity after this change. Automatic restoration of the latest change and earlier session changes is being attempted. Internet reachability is not tested.");
  };
 }
 public static LinkState Read(Guid id)
 {
  var adapter=NetworkInterface.GetAllNetworkInterfaces().SingleOrDefault(n=>Guid.TryParse(n.Id,out var candidate)&&candidate==id);
  if(adapter==null)return new(false,false,false,false);
  var properties=adapter.GetIPProperties();
  bool address=properties.UnicastAddresses.Any(a=>a.Address.AddressFamily==AddressFamily.InterNetwork&&!a.Address.ToString().StartsWith("169.254.")&&a.Address.ToString()!="0.0.0.0");
  bool gateway=properties.GatewayAddresses.Any(a=>a.Address.AddressFamily==AddressFamily.InterNetwork&&a.Address.ToString()!="0.0.0.0");
  return new(true,adapter.OperationalStatus==OperationalStatus.Up,address,gateway);
 }
}
