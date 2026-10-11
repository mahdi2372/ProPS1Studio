using System.Text;

namespace ProPS1Studio.Core.BatToExe;

/// <summary>
/// Builds the Win32 application manifest XML embedded into the generated executable.
/// Mirrors ProPS1Studio.App\app.manifest (per-monitor DPI, long paths, supportedOS entries)
/// but takes the requested execution level from the conversion request.
/// </summary>
public static class BatManifestBuilder
{
    private const string AssemblyIdentityName = "ProPS1Studio.BatLauncher";

    public static string Build(BatExecutionLevel level) => $$"""
        <?xml version="1.0" encoding="utf-8"?>
        <assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
          <assemblyIdentity version="1.0.0.0" name="{{AssemblyIdentityName}}" type="win32"/>
          <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3">
            <security>
              <requestedPrivileges>
                <requestedExecutionLevel level="{{LevelString(level)}}" uiAccess="false"/>
              </requestedPrivileges>
            </security>
          </trustInfo>
          <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
            <application>
              <!-- Windows 10 / 11 -->
              <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}"/>
              <!-- Windows 8.1 -->
              <supportedOS Id="{1f676c76-82e2-4660-8e2d-1f9f6e7a4b1f}"/>
              <!-- Windows 8 -->
              <supportedOS Id="{4a2f28e3-53b9-4441-ba9c-d69d4a4a6e38}"/>
              <!-- Windows 7 -->
              <supportedOS Id="{35138b9a-5d96-4fbd-8e2d-a2440225f93a}"/>
              <!-- Windows Vista -->
              <supportedOS Id="{e2011457-1546-43c5-a5fe-008deee3d3f0}"/>
            </application>
          </compatibility>
          <application xmlns="urn:schemas-microsoft-com:asm.v3">
            <windowsSettings>
              <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/pm</dpiAware>
              <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
              <longPathAware xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">true</longPathAware>
            </windowsSettings>
          </application>
        </assembly>
        """;

    public static string LevelString(BatExecutionLevel level) => level switch
    {
        BatExecutionLevel.RequireAdministrator => "requireAdministrator",
        BatExecutionLevel.HighestAvailable => "highestAvailable",
        _ => "asInvoker",
    };

    public static BatExecutionLevel ParseLevel(string level) => level switch
    {
        "requireAdministrator" => BatExecutionLevel.RequireAdministrator,
        "highestAvailable" => BatExecutionLevel.HighestAvailable,
        _ => BatExecutionLevel.AsInvoker,
    };
}
