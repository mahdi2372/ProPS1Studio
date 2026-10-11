using System.Text;

namespace ProPS1Studio.Core.Conversion;

/// <summary>
/// Generates the temporary ps2exe wrapper script for one conversion.
/// All values are embedded as escaped PowerShell literals so nothing depends
/// on command-line quoting.
/// </summary>
public static class Ps2exeScriptBuilder
{
    public static string Build(ConversionRequest request, Ps2exeCapability capability, string? inputOverride = null)
    {
        if (!capability.IsAvailable)
        {
            throw new InvalidOperationException("Cannot build a conversion script without an available ps2exe module.");
        }

        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("try {");
        sb.AppendLine(string.IsNullOrWhiteSpace(request.Ps2exeModulePath)
            ? "    Import-Module ps2exe -Force"
            : $"    Import-Module -Name {Q(request.Ps2exeModulePath)} -Force");
        sb.AppendLine("    $splat = @{");
        sb.AppendLine($"        in = {Q(inputOverride ?? request.InputPath)}");
        sb.AppendLine($"        outputFile = {Q(request.OutputPath)}");

        AppendFlag(sb, capability, request.RequireAdministrator, "requireAdmin");
        AppendFlag(sb, capability, request.NoConsole, "noConsole");
        AppendFlag(sb, capability, request.IconPath is not null, "iconFile", request.IconPath);
        AppendFlag(sb, capability, request.Title is not null, "title", request.Title);
        AppendFlag(sb, capability, request.Description is not null, "description", request.Description);
        AppendFlag(sb, capability, request.Company is not null, "company", request.Company);
        AppendFlag(sb, capability, request.Product is not null, "product", request.Product);
        AppendFlag(sb, capability, request.Copyright is not null, "copyright", request.Copyright);
        AppendFlag(sb, capability, request.FileVersion is not null, "version", request.FileVersion);

        sb.AppendLine("    }");
        sb.AppendLine("    Invoke-ps2exe @splat");
        sb.AppendLine("    exit 0");
        sb.AppendLine("}");
        sb.AppendLine("catch {");
        sb.AppendLine("    [Console]::Error.WriteLine($_.Exception.Message)");
        sb.AppendLine("    if ($_.InvocationInfo) { [Console]::Error.WriteLine($_.InvocationInfo.PositionMessage) }");
        sb.AppendLine("    exit 1");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>Escapes a value as a single-quoted PowerShell literal.</summary>
    public static string Q(string value) => "'" + value.Replace("'", "''") + "'";

    private static void AppendFlag(
        StringBuilder sb,
        Ps2exeCapability capability,
        bool enabled,
        string parameterName,
        string? value = null)
    {
        if (!enabled)
        {
            return;
        }

        if (!capability.Supports(parameterName))
        {
            throw new NotSupportedException(
                $"The installed ps2exe version does not support the parameter '{parameterName}'.");
        }

        if (value is null)
        {
            sb.AppendLine($"        {parameterName} = $true");
        }
        else
        {
            sb.AppendLine($"        {parameterName} = {Q(value)}");
        }
    }
}
