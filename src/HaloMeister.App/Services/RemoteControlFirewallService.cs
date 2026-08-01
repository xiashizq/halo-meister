using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace HaloMeister.App.Services;

public sealed record RemoteFirewallStatus(
    bool Exists,
    bool IsCurrent,
    string Summary);

public sealed class RemoteControlFirewallService
{
    private const string RuleName = "HaloMeister.PhoneRemote";
    private const string DisplayName = "Halo Meister Phone Remote";
    private const string Description =
        "Allows the Halo Meister phone remote from LocalSubnet and Tailscale on private networks.";

    public async Task<RemoteFirewallStatus> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            string script = $$"""
                $rule = Get-NetFirewallRule -Name '{{RuleName}}' -ErrorAction SilentlyContinue
                if ($null -eq $rule) {
                    [pscustomobject]@{ exists = $false } | ConvertTo-Json -Compress
                    exit 0
                }
                $app = $rule | Get-NetFirewallApplicationFilter
                $port = $rule | Get-NetFirewallPortFilter
                $address = $rule | Get-NetFirewallAddressFilter
                [pscustomobject]@{
                    exists = $true
                    enabled = $rule.Enabled.ToString()
                    direction = $rule.Direction.ToString()
                    action = $rule.Action.ToString()
                    profile = $rule.Profile.ToString()
                    program = $app.Program
                    protocol = $port.Protocol.ToString()
                    localPort = $port.LocalPort
                    remoteAddress = ($address.RemoteAddress -join ',')
                } | ConvertTo-Json -Compress
                """;
            ProcessResult result = await RunPowerShellAsync(
                script,
                elevate: false,
                cancellationToken);
            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                return new RemoteFirewallStatus(
                    false,
                    false,
                    "Firewall rule status could not be read.");
            }

            using JsonDocument document = JsonDocument.Parse(result.StandardOutput.Trim());
            JsonElement root = document.RootElement;
            bool exists = root.TryGetProperty("exists", out JsonElement existsValue) &&
                          existsValue.GetBoolean();
            if (!exists)
            {
                return new RemoteFirewallStatus(
                    false,
                    false,
                    $"No inbound rule is installed for TCP {RemoteControlService.Port}.");
            }

            string executable = ResolveExecutablePath();
            bool current =
                Property(root, "enabled").Equals("True", StringComparison.OrdinalIgnoreCase) &&
                Property(root, "direction").Equals("Inbound", StringComparison.OrdinalIgnoreCase) &&
                Property(root, "action").Equals("Allow", StringComparison.OrdinalIgnoreCase) &&
                Property(root, "profile").Equals("Private", StringComparison.OrdinalIgnoreCase) &&
                Property(root, "program").Equals(executable, StringComparison.OrdinalIgnoreCase) &&
                (Property(root, "protocol").Equals("TCP", StringComparison.OrdinalIgnoreCase) ||
                 Property(root, "protocol").Equals("6", StringComparison.Ordinal)) &&
                Property(root, "localPort").Equals(
                    RemoteControlService.Port.ToString(),
                    StringComparison.Ordinal) &&
                Property(root, "remoteAddress")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Contains("LocalSubnet", StringComparer.OrdinalIgnoreCase) &&
                Property(root, "remoteAddress")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Contains("100.64.0.0/10", StringComparer.OrdinalIgnoreCase);

            return new RemoteFirewallStatus(
                true,
                current,
                current
                    ? $"Private-network and Tailscale inbound rule is ready on TCP {RemoteControlService.Port}."
                    : "A Halo Meister firewall rule exists, but it does not match this executable and secure LAN scope. Configure it again.");
        }
        catch
        {
            return new RemoteFirewallStatus(
                false,
                false,
                "Firewall rule status could not be read.");
        }
    }

    public async Task InstallAsync(CancellationToken cancellationToken = default)
    {
        string executable = PowerShellLiteral(ResolveExecutablePath());
        string script = $$"""
            $ErrorActionPreference = 'Stop'
            try {
                Get-NetFirewallRule -Name '{{RuleName}}' -ErrorAction SilentlyContinue |
                    Remove-NetFirewallRule -ErrorAction Stop
                New-NetFirewallRule `
                    -Name '{{RuleName}}' `
                    -DisplayName '{{DisplayName}}' `
                    -Description '{{Description}}' `
                    -Direction Inbound `
                    -Action Allow `
                    -Enabled True `
                    -Profile Private `
                    -Program '{{executable}}' `
                    -Protocol TCP `
                    -LocalPort {{RemoteControlService.Port}} `
                    -RemoteAddress LocalSubnet,100.64.0.0/10 | Out-Null
                exit 0
            }
            catch {
                Write-Error $_
                exit 1
            }
            """;
        ProcessResult result = await RunPowerShellAsync(
            script,
            elevate: true,
            cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "Windows could not configure the Halo Meister firewall rule.");
        }
    }

    public async Task RemoveAsync(CancellationToken cancellationToken = default)
    {
        string script = $$"""
            $ErrorActionPreference = 'Stop'
            try {
                Get-NetFirewallRule -Name '{{RuleName}}' -ErrorAction SilentlyContinue |
                    Remove-NetFirewallRule -ErrorAction Stop
                exit 0
            }
            catch {
                Write-Error $_
                exit 1
            }
            """;
        ProcessResult result = await RunPowerShellAsync(
            script,
            elevate: true,
            cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "Windows could not remove the Halo Meister firewall rule.");
        }
    }

    private static async Task<ProcessResult> RunPowerShellAsync(
        string script,
        bool elevate,
        CancellationToken cancellationToken)
    {
        string powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        if (!File.Exists(powershell))
            throw new FileNotFoundException("Windows PowerShell was not found.", powershell);

        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var start = new ProcessStartInfo
        {
            FileName = powershell,
            UseShellExecute = elevate,
            CreateNoWindow = !elevate,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(encoded);
        if (elevate)
        {
            start.Verb = "runas";
        }
        else
        {
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
        }

        Process process;
        try
        {
            process = Process.Start(start)
                ?? throw new InvalidOperationException("Windows PowerShell did not start.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException(
                "Administrator approval was cancelled.",
                ex,
                cancellationToken);
        }

        using (process)
        {
            Task<string>? outputTask = elevate
                ? null
                : process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string>? errorTask = elevate
                ? null
                : process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return new ProcessResult(
                process.ExitCode,
                outputTask is null ? "" : await outputTask,
                errorTask is null ? "" : await errorTask);
        }
    }

    private static string ResolveExecutablePath() =>
        Environment.ProcessPath
        ?? throw new InvalidOperationException(
            "Halo Meister could not determine its executable path.");

    private static string PowerShellLiteral(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    private static string Property(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value)
            ? value.GetString() ?? ""
            : "";

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
