using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SawnTask.Core;

public sealed record PowerPlan(Guid Id, string Name, bool Active);

// One editable value of a plan. Unit says how it is written: seconds (0 = never) or percent.
public sealed record PowerSetting(string TextKey, Guid SubGroup, Guid Id, PowerUnit Unit);

public enum PowerUnit { Seconds, Percent }

// Windows power plans (Balanced, High performance, ...): list, switch, copy, rename, delete,
// and edit a few settings. All of it is powrprof.dll, the same API powercfg.exe uses.
public static class PowerPlans
{
    // Subgroup and setting GUIDs are fixed by Windows (powercfg -query lists them).
    static readonly Guid Video = new("7516b95f-f776-4464-8c53-06167f40cc99");
    static readonly Guid Sleep = new("238c9fa8-0aad-41ed-83f4-97be242c8f20");
    static readonly Guid Processor = new("54533251-82be-4824-96c1-47b60b740d00");

    public static readonly PowerSetting[] Settings =
    [
        new("PP.Display", Video, new("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e"), PowerUnit.Seconds),
        new("PP.Sleep", Sleep, new("29f6c1db-86da-48c5-9fdb-f2b67b1f44da"), PowerUnit.Seconds),
        new("PP.Hibernate", Sleep, new("9d7815a6-7ee4-497e-8888-515a05f02364"), PowerUnit.Seconds),
        new("PP.MinCpu", Processor, new("893dee8e-2bef-41e0-89c6-b55d0929964c"), PowerUnit.Percent),
        new("PP.MaxCpu", Processor, new("bc5038f7-23e0-4960-96da-33abaf5935ec"), PowerUnit.Percent),
    ];

    // The values offered for each unit (the plan's current value is added if it is not one of them).
    public static readonly uint[] SecondChoices = [0, 60, 120, 300, 600, 900, 1200, 1800, 2700, 3600, 7200, 10800, 18000];
    public static readonly uint[] PercentChoices = [.. Enumerable.Range(0, 21).Select(i => (uint)(i * 5))];

    const uint AccessScheme = 16, NoMoreItems = 259;

    [DllImport("powrprof.dll")] static extern uint PowerEnumerate(IntPtr root, IntPtr scheme, IntPtr subGroup, uint flags, uint index, IntPtr buffer, ref uint size);
    [DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof.dll")] static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
    [DllImport("powrprof.dll")] static extern uint PowerDuplicateScheme(IntPtr root, ref Guid source, out IntPtr copy);
    [DllImport("powrprof.dll")] static extern uint PowerDeleteScheme(IntPtr root, ref Guid scheme);
    [DllImport("powrprof.dll")] static extern uint PowerReadFriendlyName(IntPtr root, ref Guid scheme, IntPtr subGroup, IntPtr setting, IntPtr buffer, ref uint size);
    [DllImport("powrprof.dll")] static extern uint PowerWriteFriendlyName(IntPtr root, ref Guid scheme, IntPtr subGroup, IntPtr setting, byte[] buffer, uint size);
    [DllImport("powrprof.dll")] static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid subGroup, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid subGroup, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid subGroup, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid subGroup, ref Guid setting, uint value);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);

    static void Check(uint status)
    {
        if (status != 0) throw new Win32Exception((int)status);
    }

    public static Guid? ActiveId()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out IntPtr pointer) != 0 || pointer == IntPtr.Zero) return null;
        try { return Marshal.PtrToStructure<Guid>(pointer); }
        finally { LocalFree(pointer); }
    }

    public static List<PowerPlan> List()
    {
        var active = ActiveId();
        var plans = new List<PowerPlan>();
        IntPtr buffer = Marshal.AllocHGlobal(16);
        try
        {
            for (uint i = 0; i < 500; i++) // a safety limit; Windows has a few dozen
            {
                uint size = 16;
                uint status = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, AccessScheme, i, buffer, ref size);
                if (status == NoMoreItems) break;
                Check(status);
                var id = Marshal.PtrToStructure<Guid>(buffer);
                string name = Name(id);
                if (DemoEnglish && plans.Any(p => p.Name == name)) name += " (copy)"; // two plans may share a name
                plans.Add(new PowerPlan(id, name, id == active));
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return plans;
    }

    public static string Name(Guid plan)
    {
        uint size = 0;
        PowerReadFriendlyName(IntPtr.Zero, ref plan, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size);
        if (size == 0) return plan.ToString();
        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            return PowerReadFriendlyName(IntPtr.Zero, ref plan, IntPtr.Zero, IntPtr.Zero, buffer, ref size) == 0
                ? Demo(Marshal.PtrToStringUni(buffer) ?? plan.ToString())
                : plan.ToString();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public static void Activate(Guid plan) => Check(PowerSetActiveScheme(IntPtr.Zero, ref plan));

    // A copy of an existing plan (this is how powercfg -duplicatescheme makes a new one).
    public static Guid Duplicate(Guid source, string name)
    {
        Check(PowerDuplicateScheme(IntPtr.Zero, ref source, out IntPtr pointer));
        try
        {
            var copy = Marshal.PtrToStructure<Guid>(pointer);
            Rename(copy, name);
            return copy;
        }
        finally { LocalFree(pointer); }
    }

    public static void Rename(Guid plan, string name)
    {
        byte[] bytes = System.Text.Encoding.Unicode.GetBytes(name + "\0");
        Check(PowerWriteFriendlyName(IntPtr.Zero, ref plan, IntPtr.Zero, IntPtr.Zero, bytes, (uint)bytes.Length));
    }

    // Windows refuses to delete the plan in use: activate another one first.
    public static void Delete(Guid plan) => Check(PowerDeleteScheme(IntPtr.Zero, ref plan));

    public static uint Read(Guid plan, PowerSetting s, bool pluggedIn) => Read(plan, s.SubGroup, s.Id, pluggedIn);

    public static uint Read(Guid plan, Guid sub, Guid id, bool pluggedIn)
    {
        uint value;
        Check(pluggedIn
            ? PowerReadACValueIndex(IntPtr.Zero, ref plan, ref sub, ref id, out value)
            : PowerReadDCValueIndex(IntPtr.Zero, ref plan, ref sub, ref id, out value));
        return value;
    }

    public static void Write(Guid plan, PowerSetting s, bool pluggedIn, uint value) => Write(plan, s.SubGroup, s.Id, pluggedIn, value);

    // Writes the value; if the plan is the active one it is activated again so the change takes effect.
    public static void Write(Guid plan, Guid sub, Guid id, bool pluggedIn, uint value)
    {
        Check(pluggedIn
            ? PowerWriteACValueIndex(IntPtr.Zero, ref plan, ref sub, ref id, value)
            : PowerWriteDCValueIndex(IntPtr.Zero, ref plan, ref sub, ref id, value));
        if (ActiveId() == plan) Activate(plan);
    }

    // ---- "Change advanced power settings": every visible subgroup and setting of a plan ----

    const uint AccessSubgroup = 17, AccessIndividualSetting = 18, AttributeHide = 1;

    [DllImport("powrprof.dll", EntryPoint = "PowerEnumerate")]
    static extern uint EnumerateGroups(IntPtr root, ref Guid scheme, IntPtr subGroup, uint flags, uint index, IntPtr buffer, ref uint size);
    [DllImport("powrprof.dll", EntryPoint = "PowerEnumerate")]
    static extern uint EnumerateSettings(IntPtr root, ref Guid scheme, ref Guid subGroup, uint flags, uint index, IntPtr buffer, ref uint size);
    [DllImport("powrprof.dll", EntryPoint = "PowerReadFriendlyName")]
    static extern uint ReadGroupName(IntPtr root, IntPtr scheme, ref Guid subGroup, IntPtr setting, IntPtr buffer, ref uint size);
    [DllImport("powrprof.dll", EntryPoint = "PowerReadFriendlyName")]
    static extern uint ReadSettingName(IntPtr root, IntPtr scheme, ref Guid subGroup, ref Guid setting, IntPtr buffer, ref uint size);
    [DllImport("powrprof.dll", EntryPoint = "PowerReadDescription")]
    static extern uint ReadGroupDescription(IntPtr root, IntPtr scheme, ref Guid subGroup, IntPtr setting, IntPtr buffer, ref uint size);
    [DllImport("powrprof.dll", EntryPoint = "PowerReadDescription")]
    static extern uint ReadSettingDescription(IntPtr root, IntPtr scheme, ref Guid subGroup, ref Guid setting, IntPtr buffer, ref uint size);
    [DllImport("powrprof.dll", EntryPoint = "PowerReadSettingAttributes")]
    static extern uint GroupAttributes(ref Guid subGroup, IntPtr setting);
    [DllImport("powrprof.dll", EntryPoint = "PowerReadSettingAttributes")]
    static extern uint SettingAttributes(ref Guid subGroup, ref Guid setting);
    [DllImport("powrprof.dll")] static extern uint PowerReadPossibleFriendlyName(IntPtr root, ref Guid subGroup, ref Guid setting, uint index, IntPtr buffer, ref uint size);
    [DllImport("powrprof.dll")] static extern uint PowerReadValueMin(IntPtr root, ref Guid subGroup, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] static extern uint PowerReadValueMax(IntPtr root, ref Guid subGroup, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] static extern uint PowerReadValueIncrement(IntPtr root, ref Guid subGroup, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] static extern uint PowerReadValueUnitsSpecifier(IntPtr root, ref Guid subGroup, ref Guid setting, IntPtr buffer, ref uint size);

    // For README screenshots only: SAWNTASK_DEMO_ENGLISH=1 shows the Windows-supplied names in English
    // (a Turkish Windows gives Turkish text, which a reader of the English README would not follow).
    static readonly bool DemoEnglish = Environment.GetEnvironmentVariable("SAWNTASK_DEMO_ENGLISH") == "1";
    static readonly Dictionary<string, string> DemoText = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Dengeli"] = "Balanced", ["Yüksek performans"] = "High performance", ["Güç tasarrufu"] = "Power saver",
        ["Nihai Performans"] = "Ultimate Performance", ["Sabit disk"] = "Hard disk",
        ["Sabit diski kapatmak için beklenecek süre"] = "Turn off hard disk after",
        ["JavaScript Zamanlayıcı Sıklığı"] = "JavaScript Timer Frequency", ["En Fazla Güç Tasarrufu"] = "Maximum Power Saving",
        ["En Yüksek Performans"] = "Maximum Performance", ["Masaüstü arka plan ayarları"] = "Desktop background settings",
        ["Slayt gösterisi"] = "Slide show", ["Duraklatıldı"] = "Paused", ["Kullanılabilir"] = "Available",
        ["Kablosuz Bağdaştırıcı Ayarları"] = "Wireless Adapter Settings", ["Güç Tasarrufu Modu"] = "Power Saving Mode",
        ["Maksimum Performans"] = "Maximum Performance", ["Düşük Güç Tasarrufu"] = "Low Power Saving",
        ["Orta Güç Tasarrufu"] = "Medium Power Saving", ["Maksimum Güç Tasarrufu"] = "Maximum Power Saving",
    };

    static string Demo(string text) => DemoEnglish && DemoText.TryGetValue(text.Trim(), out var english) ? english : text;

    // powrprof's "read a string" functions are called twice: once for the size, once for the text.
    delegate uint StringReader(IntPtr buffer, ref uint size);

    static string ReadString(StringReader read)
    {
        uint size = 0;
        if (read(IntPtr.Zero, ref size) != 0 || size == 0) return "";
        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try { return read(buffer, ref size) == 0 ? Demo(Marshal.PtrToStringUni(buffer) ?? "") : ""; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    // The subgroups of a plan (subGroup null), or the settings of one subgroup.
    static List<Guid> Enumerate(Guid plan, Guid? subGroup)
    {
        var result = new List<Guid>();
        IntPtr buffer = Marshal.AllocHGlobal(16);
        try
        {
            for (uint i = 0; ; i++)
            {
                uint size = 16;
                uint status = subGroup is Guid sub
                    ? EnumerateSettings(IntPtr.Zero, ref plan, ref sub, AccessIndividualSetting, i, buffer, ref size)
                    : EnumerateGroups(IntPtr.Zero, ref plan, IntPtr.Zero, AccessSubgroup, i, buffer, ref size);
                if (status != 0) break; // ERROR_NO_MORE_ITEMS
                result.Add(Marshal.PtrToStructure<Guid>(buffer));
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return result;
    }

    // Every setting Windows shows in "Change advanced power settings" (hidden ones are skipped),
    // with its name and explanation in the Windows display language.
    public static List<AdvancedGroup> Advanced(Guid plan)
    {
        var groups = new List<AdvancedGroup>();
        foreach (Guid group in Enumerate(plan, null))
        {
            Guid sub = group;
            if ((GroupAttributes(ref sub, IntPtr.Zero) & AttributeHide) != 0) continue;
            var settings = new List<AdvancedSetting>();
            foreach (Guid setting in Enumerate(plan, sub))
            {
                Guid id = setting;
                if ((SettingAttributes(ref sub, ref id) & AttributeHide) != 0) continue;
                string name = ReadString((IntPtr b, ref uint n) => ReadSettingName(IntPtr.Zero, IntPtr.Zero, ref sub, ref id, b, ref n));
                if (name.Length == 0) continue;
                string description = ReadString((IntPtr b, ref uint n) => ReadSettingDescription(IntPtr.Zero, IntPtr.Zero, ref sub, ref id, b, ref n));

                // Either a number range (it has a minimum and maximum) or named choices, where the
                // stored value is the choice's index. Range settings also answer the "choice name"
                // call for any index, so the range is checked first.
                var options = new List<PowerOption>();
                uint min = 0, max = 0, step = 1;
                string units = "";
                bool range = PowerReadValueMin(IntPtr.Zero, ref sub, ref id, out min) == 0
                             & PowerReadValueMax(IntPtr.Zero, ref sub, ref id, out max) == 0 && max > min;
                if (range)
                {
                    if (PowerReadValueIncrement(IntPtr.Zero, ref sub, ref id, out step) != 0 || step == 0) step = 1;
                    units = ReadString((IntPtr b, ref uint n) => PowerReadValueUnitsSpecifier(IntPtr.Zero, ref sub, ref id, b, ref n));
                }
                else
                {
                    for (uint i = 0; i < 100; i++) // a safety limit; real lists have a handful
                    {
                        uint index = i;
                        string option = ReadString((IntPtr b, ref uint n) => PowerReadPossibleFriendlyName(IntPtr.Zero, ref sub, ref id, index, b, ref n));
                        if (option.Length == 0) break;
                        options.Add(new PowerOption(i, option));
                    }
                }
                settings.Add(new AdvancedSetting(sub, id, name, description, options, min, max, step, units));
            }
            if (settings.Count == 0) continue;
            string groupName = ReadString((IntPtr b, ref uint n) => ReadGroupName(IntPtr.Zero, IntPtr.Zero, ref sub, IntPtr.Zero, b, ref n));
            string groupText = ReadString((IntPtr b, ref uint n) => ReadGroupDescription(IntPtr.Zero, IntPtr.Zero, ref sub, IntPtr.Zero, b, ref n));
            groups.Add(new AdvancedGroup(sub, groupName.Length > 0 ? groupName : sub.ToString(), groupText, settings));
        }
        return groups;
    }
}

public sealed record PowerOption(uint Value, string Name);

// One advanced setting: either named Options, or a number from Min to Max in steps of Step.
public sealed record AdvancedSetting(Guid SubGroup, Guid Id, string Name, string Description,
    IReadOnlyList<PowerOption> Options, uint Min, uint Max, uint Step, string Units);

public sealed record AdvancedGroup(Guid Id, string Name, string Description, IReadOnlyList<AdvancedSetting> Settings);
