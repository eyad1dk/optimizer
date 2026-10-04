using System.IO;
namespace ForgePC;

public static class OperationOrdering
{
 public static List<Change> Order(IEnumerable<Change> input)
 {
  var result=input.ToList();
  if(result.Any(c=>c.Key=="power")&&result.Any(c=>ProcessorPower.Ids.Contains(c.Key)))throw new InvalidOperationException("Switch power plans in a separate session, then preview processor limits again.");
  foreach(var suffix in new[]{"ac","dc"})
  {
   var min=result.SingleOrDefault(c=>c.Key=="cpu-min-"+suffix);var max=result.SingleOrDefault(c=>c.Key=="cpu-max-"+suffix);
   if(min==null||max==null)continue;
   var minBefore=ProcessorPower.Parse(min.Before);var maxBefore=ProcessorPower.Parse(max.Before);var minAfter=ProcessorPower.Parse(min.After);var maxAfter=ProcessorPower.Parse(max.After);
   if(minBefore.Scheme!=maxBefore.Scheme||minBefore.Scheme!=minAfter.Scheme||minAfter.Scheme!=maxAfter.Scheme)throw new InvalidDataException("Processor boundaries must belong to the same captured scheme.");
   if(minBefore.Percent>maxBefore.Percent||minAfter.Percent>maxAfter.Percent)throw new InvalidDataException("Minimum processor state exceeds maximum.");
   Change first=minAfter.Percent>maxBefore.Percent?max:min;
   Change second=first==min?max:min;
   var position=Math.Min(result.IndexOf(min),result.IndexOf(max));result.Remove(min);result.Remove(max);result.Insert(position,first);result.Insert(position+1,second);
  }
  return result;
 }
}
