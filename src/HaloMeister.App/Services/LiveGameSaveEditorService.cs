using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using HaloMeister.App.Localization;

namespace HaloMeister.App.Services;

public sealed record LiveWeaponAmmo(
    int InventorySlot,
    string SlotName,
    string WeaponName,
    string ActorPath,
    long ComponentAddress,
    int ReserveAmmo,
    int ReserveMaximum,
    int LoadedAmmo,
    int LoadedMaximum,
    string? ScenarioCode);

public sealed partial class LiveGameSaveEditorService
{
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int MagazineArrayOffset = 0x3C0;
    private readonly ScriptingBridgeService _bridge = ScriptingBridgeService.Current;

    public async Task<IReadOnlyList<LiveWeaponAmmo>> CaptureLoadoutAsync(
        CancellationToken cancellationToken = default)
    {
        const string script = """
            local lines = {}
            local inventory_class = StaticFindObject(
                "/Script/BlamSynchronization.BlamUnitInventoryComponent"
            )
            local weapon_class = StaticFindObject(
                "/Script/BlamSynchronization.BlamWeaponComponent"
            )
            for _, unit in ipairs(FindAllOf("BlamUnitComponent") or {}) do
                if unit:IsValid() and unit:IsControlledByAnyPlayer() then
                    local owner = unit:GetOwner()
                    local inventory = owner:GetComponentByClass(inventory_class)
                    if inventory and inventory:IsValid() then
                        for slot = 0, 3 do
                            local weapon = inventory:GetWeapon(slot)
                            if weapon and weapon:IsValid() then
                                local component = weapon:GetComponentByClass(weapon_class)
                                if component and component:IsValid()
                                    and component:GetMagazineCount() > 0 then
                                    table.insert(lines, string.format(
                                        "%d\t%s\t%s",
                                        slot,
                                        tostring(component:GetAddress()),
                                        weapon:GetFullName()
                                    ))
                                end
                            end
                        end
                    end
                end
            end
            return table.concat(lines, "\n")
            """;

        ScriptExecutionResult result = await _bridge.ExecuteAsync(
            ScriptLanguage.Lua,
            script,
            cancellationToken: cancellationToken);
        // Lua is the one language the bridge can genuinely confirm, so require that here.
        if (result.Outcome != ScriptOutcome.Confirmed)
            throw new InvalidOperationException(result.Message);

        const string marker = "Return value: ";
        int markerOffset = result.Message.IndexOf(marker, StringComparison.Ordinal);
        if (markerOffset < 0)
            throw new InvalidOperationException(
                "The game is running, but no player weapon loadout was returned. Resume a campaign checkpoint first.");

        string response = result.Message[(markerOffset + marker.Length)..].Trim();
        Process process = Process.GetProcessesByName("HaloCampaignEvolved").SingleOrDefault()
            ?? throw new InvalidOperationException(L.Get("shell.game_not_running"));
        nint handle = OpenProcess(
            ProcessVmRead | ProcessQueryLimitedInformation,
            inheritHandle: false,
            process.Id);
        if (handle == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the running game process.");

        try
        {
            var loadout = new List<LiveWeaponAmmo>();
            foreach (string line in response.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] fields = line.TrimEnd('\r').Split('\t', 3);
                if (fields.Length != 3 ||
                    !int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int slot) ||
                    !long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long address))
                    throw new InvalidDataException($"The live weapon response is invalid: {line}");

                MagazineState magazine = ReadMagazine(handle, address);
                string actorPath = fields[2];
                loadout.Add(new LiveWeaponAmmo(
                    slot,
                    SlotDisplay(slot),
                    WeaponDisplay(actorPath),
                    actorPath,
                    address,
                    magazine.Reserve,
                    magazine.ReserveMaximum,
                    magazine.Loaded,
                    magazine.LoadedMaximum,
                    ScenarioCode(actorPath)));
            }

            if (loadout.Count == 0)
                throw new InvalidOperationException(
                    "No magazine-based player weapons were found. Resume a campaign checkpoint and try again.");
            return loadout.OrderBy(item => item.InventorySlot).ToArray();
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static MagazineState ReadMagazine(nint process, long componentAddress)
    {
        byte[] component = ReadMemory(process, componentAddress + MagazineArrayOffset, 16);
        long arrayAddress = BinaryPrimitives.ReadInt64LittleEndian(component);
        int count = BinaryPrimitives.ReadInt32LittleEndian(component.AsSpan(8));
        if (arrayAddress == 0 || count is < 1 or > 8)
            throw new InvalidDataException("The live weapon has an invalid magazine array.");

        byte[] magazine = ReadMemory(process, arrayAddress, 24);
        int reserve = BinaryPrimitives.ReadInt32LittleEndian(magazine);
        int reserveMaximum = BinaryPrimitives.ReadInt32LittleEndian(magazine.AsSpan(4));
        int loaded = BinaryPrimitives.ReadInt32LittleEndian(magazine.AsSpan(8));
        int loadedMaximum = BinaryPrimitives.ReadInt32LittleEndian(magazine.AsSpan(12));
        if (reserve is < 0 or > 100_000 || loaded is < 0 or > 100_000 ||
            reserveMaximum < reserve || loadedMaximum < loaded)
            throw new InvalidDataException("The live weapon returned implausible ammunition values.");
        return new MagazineState(reserve, reserveMaximum, loaded, loadedMaximum);
    }

    private static byte[] ReadMemory(nint process, long address, int count)
    {
        var result = new byte[count];
        if (!ReadProcessMemory(process, address, result, result.Length, out nuint read) ||
            read != (nuint)result.Length)
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Could not read live game memory at 0x{address:X}.");
        return result;
    }

    private static string SlotDisplay(int slot) => slot switch
    {
        0 => "Primary / equipped",
        1 => "Secondary",
        2 => "Backpack",
        3 => "Other backpack",
        _ => $"Inventory slot {slot}",
    };

    private static string WeaponDisplay(string actorPath)
    {
        Match match = WeaponActorRegex().Match(actorPath);
        if (!match.Success) return "Unknown weapon";
        string compact = match.Groups[1].Value.Replace('_', ' ');
        return WordBoundaryRegex().Replace(compact, "$1 $2").Trim() switch
        {
            "Assault Rifle" => "Assault Rifle",
            "Rocket Launcher" => "Rocket Launcher",
            string value => value,
        };
    }

    private static string? ScenarioCode(string actorPath)
    {
        Match match = ScenarioRegex().Match(actorPath);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    [GeneratedRegex(@"BP_([A-Za-z0-9_]+?)_WeaponActor", RegexOptions.CultureInvariant)]
    private static partial Regex WeaponActorRegex();

    [GeneratedRegex(@"([a-z0-9])([A-Z])", RegexOptions.CultureInvariant)]
    private static partial Regex WordBoundaryRegex();

    [GeneratedRegex(@"/Solo/([^/]+)/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ScenarioRegex();

    private sealed record MagazineState(
        int Reserve,
        int ReserveMaximum,
        int Loaded,
        int LoadedMaximum);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(
        nint process,
        long baseAddress,
        [Out] byte[] buffer,
        int size,
        out nuint bytesRead);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
