using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
namespace ForgePC;
public record UpdateResult(string Status, string Message, Uri? ReleasePage, Version? Version);
public interface IUpdateService { Task<UpdateResult> CheckAsync(bool includePreviews,CancellationToken token = default); }
public sealed class UpdateService(HttpClient? client = null) : IUpdateService
{
 public const string ReleasesPage = "https://github.com/eyad1dk/optimizer/releases";
 private readonly HttpClient http = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(12) };
 public static bool IsOfficialPage(Uri page) => page.Scheme == Uri.UriSchemeHttps && page.Host == "github.com" &&
  page.AbsolutePath.StartsWith("/eyad1dk/optimizer/releases/",StringComparison.Ordinal) && string.IsNullOrEmpty(page.UserInfo) && page.IsDefaultPort;
 public static UpdateResult Parse(string json,bool previews,Version current)
 {
  if (json.Length > 1048576) return new("Invalid","Release metadata is too large.",null,null);
  try
  {
   using var doc = JsonDocument.Parse(json);
   if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException();
   var releases = new List<(Version Version,Uri Page)>();
   foreach (var item in doc.RootElement.EnumerateArray().Take(50))
   {
    if (item.GetProperty("draft").GetBoolean() || (!previews && item.GetProperty("prerelease").GetBoolean())) continue;
    var tag = item.GetProperty("tag_name").GetString() ?? "";
    if (!Version.TryParse(tag.TrimStart('v').Split('-')[0],out var version)) continue;
    if (!Uri.TryCreate(item.GetProperty("html_url").GetString(),UriKind.Absolute,out var page) || !IsOfficialPage(page)) continue;
    releases.Add((version,page));
   }
   var latest = releases.OrderByDescending(r => r.Version).FirstOrDefault();
   if (latest.Version == null) return new("No releases","No eligible release was found for this channel.",null,null);
   return latest.Version > current
    ? new("Available",$"Version {latest.Version} is available. Review the release page; downloads are never installed automatically.",latest.Page,latest.Version)
    : new("Current",$"No newer release than {current} was found in this channel.",latest.Page,latest.Version);
  }
  catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
  { return new("Invalid","The release response could not be validated.",null,null); }
 }
 public async Task<UpdateResult> CheckAsync(bool includePreviews,CancellationToken token = default)
 {
  using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(12));token=deadline.Token;
  try
  {
   using var request = new HttpRequestMessage(HttpMethod.Get,"https://api.github.com/repos/eyad1dk/optimizer/releases?per_page=30");
   request.Headers.UserAgent.ParseAdd("EZoptimizer/2.1.0");
   request.Headers.Accept.ParseAdd("application/vnd.github+json");
   using var response = await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token);
   if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
    return new("Rate limited","GitHub refused this check. Try later or open the release page.",null,null);
   if (!response.IsSuccessStatusCode) return new("Unavailable","GitHub releases are unavailable. No files were downloaded.",null,null);
   if (response.Content.Headers.ContentLength > 1048576) return new("Invalid","Release metadata exceeds the size limit.",null,null);
   await using var stream = await response.Content.ReadAsStreamAsync(token);
   using var bounded = new MemoryStream(); var buffer = new byte[8192];
   int count; while ((count = await stream.ReadAsync(buffer,token)) != 0)
   { if (bounded.Length + count > 1048576) return new("Invalid","Release metadata exceeds the size limit.",null,null); bounded.Write(buffer,0,count); }
   return Parse(System.Text.Encoding.UTF8.GetString(bounded.ToArray()),includePreviews,new Version(2,0,0));
  }
  catch (OperationCanceledException) { return new("Offline or timeout","The check timed out or was cancelled. You can keep using the app offline.",null,null); }
  catch (HttpRequestException) { return new("Offline","Could not reach GitHub. No update was downloaded.",null,null); }
  catch (IOException) { return new("Interrupted","The metadata response was interrupted. No executable was downloaded or replaced.",null,null); }
 }
 public static void InstallDownloadedArtifact() => throw new NotSupportedException("Automatic replacement is disabled: no publisher signing trust chain is configured.");
}
