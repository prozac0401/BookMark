using System.Security.Principal;
namespace WorkBookmark.Browser;
internal static class BrowserPipeName
{
 internal static string Value => "WorkBookmark_Browser_" + WindowsIdentity.GetCurrent().User!.Value + "_" + System.Diagnostics.Process.GetCurrentProcess().SessionId;
}
