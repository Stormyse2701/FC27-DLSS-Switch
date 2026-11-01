// FC 27 DLSS Switch v1.2
// Switches DLSS Super Resolution / DLAA and the engine's own AMD FSR 3 upscaler in EA SPORTS FC 27 at runtime
// via global hotkeys and a tray menu. FSR 3 runs on every GPU; DLSS is offered only when an NVIDIA GPU is present.
// It writes the upscaler mode into the game's WorldRenderSettings objects. Field offsets, enum values and the
// objects' vtables are looked up by name in the engine's reflection data, so the tool adapts to game updates.
// v1.2: every object is identified by its type and checked field by field (flags, enums, floats), looked at twice
// before the first write and checked again before every write; addresses from earlier sessions are never reused.
// Requires the NVIDIA Streamline/DLSS DLLs in the game folder (installed by Installieren.bat).
// Compiles with the C# 5 compiler that ships with Windows (.NET Framework 4.x), see build.ps1.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("FC 27 DLSS Switch")]
[assembly: AssemblyProduct("FC 27 DLSS Mod")]
[assembly: AssemblyDescription("DLSS / DLAA / FSR 3 switch for EA SPORTS FC 27")]
[assembly: AssemblyVersion("1.2.0.0")]
[assembly: AssemblyFileVersion("1.2.0.0")]

public static class Program
{
    [STAThread]
    public static void Main()
    {
        bool created;
        using (Mutex mx = new Mutex(true, "FC27_DLSS_Switch_v1", out created))
        {
            if (!created)
            {
                MessageBox.Show("The FC 27 DLSS Switch is already running.\nYou can find its icon in the bottom right of the taskbar.", "FC 27 DLSS Switch", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Start(AppDomain.CurrentDomain.BaseDirectory);
        }
    }

    public static void Start(string dir)
    {
        string crashLog = Path.Combine(dir, "FC27_DLSS_Switch_errors.txt");
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
        {
            try { File.AppendAllText(crashLog, DateTime.Now.ToString("s") + "  " + e.Exception + Environment.NewLine); } catch { }
        };
        AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
        {
            try { File.AppendAllText(crashLog, DateTime.Now.ToString("s") + "  " + e.ExceptionObject + Environment.NewLine); } catch { }
        };
        Application.EnableVisualStyles();
        try { Application.SetCompatibleTextRenderingDefault(false); } catch { }
        Application.Run(new DlssSwitch(dir));
    }
}

// ---------------------------------------------------------------------------------------------
// Access to the game process
// ---------------------------------------------------------------------------------------------
public class Proc
{
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr written);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr VirtualQueryEx(IntPtr h, IntPtr addr, out MBI info, IntPtr len);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr h, out uint code);

    [StructLayout(LayoutKind.Sequential)]
    public struct MBI { public ulong BaseAddress; public ulong AllocationBase; public uint AllocationProtect; public uint pad1; public ulong RegionSize; public uint State; public uint Protect; public uint Type; public uint pad2; }

    public IntPtr H = IntPtr.Zero;
    public int Pid;
    public ulong Base, ImgSize;
    public int LastError;

    public static Proc Open(int pid)
    {
        Proc p = new Proc();
        p.Pid = pid;
        p.H = OpenProcess(0x0010 | 0x0020 | 0x0008 | 0x0400, false, pid);
        if (p.H == IntPtr.Zero) p.LastError = Marshal.GetLastWin32Error();
        return p;
    }
    public void Close() { if (H != IntPtr.Zero) { CloseHandle(H); H = IntPtr.Zero; } }
    public virtual bool Alive() { uint c; return H != IntPtr.Zero && GetExitCodeProcess(H, out c) && c == 259; }
    public virtual byte[] Read(ulong a, int n)
    {
        if (n <= 0 || H == IntPtr.Zero) return null;
        byte[] b = new byte[n];
        IntPtr got;
        if (!ReadProcessMemory(H, (IntPtr)(long)a, b, (IntPtr)n, out got) || (long)got != n) return null;
        return b;
    }
    public virtual int ReadInto(ulong a, byte[] buf, int n)
    {
        IntPtr got;
        if (H == IntPtr.Zero || !ReadProcessMemory(H, (IntPtr)(long)a, buf, (IntPtr)n, out got)) return 0;
        return (int)(long)got;
    }
    public virtual bool Write32(ulong a, uint v)
    {
        byte[] b = BitConverter.GetBytes(v);
        IntPtr w;
        return H != IntPtr.Zero && WriteProcessMemory(H, (IntPtr)(long)a, b, (IntPtr)4, out w) && (long)w == 4;
    }
    public virtual bool Query(ulong a, out MBI m)
    {
        return VirtualQueryEx(H, (IntPtr)(long)a, out m, (IntPtr)Marshal.SizeOf(typeof(MBI))) != IntPtr.Zero;
    }
    public bool InImage(ulong a) { return a >= Base && a < Base + ImgSize; }
}

// ---------------------------------------------------------------------------------------------
// Where the settings live inside a WorldRenderSettings object and which values are valid
// ---------------------------------------------------------------------------------------------
public class WrsLayout
{
    public int OffCs, OffUp, OffFg, OffN, OffFs, Size;
    public uint[] Up = new uint[6];             // DLSS:  index 1..5 = Ultra Performance .. DLAA
    public uint[] UpFsr = new uint[6];          // FSR 3: index 1..5 = Ultra Performance .. Native AA
    public bool HasFsr;
    public uint FsNone, FsTu, FgOff;
    public Dictionary<uint, bool> UpSet = new Dictionary<uint, bool>();
    public Dictionary<uint, bool> FsSet = new Dictionary<uint, bool>();
    public Dictionary<uint, bool> FgSet = new Dictionary<uint, bool>();
    public List<int> Bools = new List<int>();
    public List<int> EnumOffs = new List<int>();
    public List<Dictionary<uint, bool>> EnumSets = new List<Dictionary<uint, bool>>();
    public List<int> Floats = new List<int>();
    public int MinTrue;
    public ulong NameStr;
    public string Source = "";
    public bool Strong;          // true = every flag / enum / float field of the class is checked (engine data available)
    public WrsLayout Full;       // the complete engine layout when this one is limited to a smaller window (diagnostics)

    static uint U32(byte[] b, int o) { return BitConverter.ToUInt32(b, o); }

    public const int WHY_OK = 0, WHY_TYPE = 1, WHY_MAIN = 2, WHY_ENUM = 3, WHY_FLAG = 4, WHY_FLOAT = 5;
    public static readonly string[] WhyText = { "ok", "type", "upscaler fields", "enum fields", "flag fields", "float fields" };

    // Structural check of a candidate object that starts at b[o]. Returns WHY_OK or the first failed group.
    public int Why(byte[] b, int o, Proc p)
    {
        if (b == null || o < 0 || o + Size > b.Length) return WHY_TYPE;
        if (!p.InImage(BitConverter.ToUInt64(b, o))) return WHY_TYPE;
        if (!UpSet.ContainsKey(U32(b, o + OffUp))) return WHY_MAIN;
        if (!FsSet.ContainsKey(U32(b, o + OffFs))) return WHY_MAIN;
        if (!FgSet.ContainsKey(U32(b, o + OffFg))) return WHY_MAIN;
        if (U32(b, o + OffN) > 8) return WHY_MAIN;
        float cs = BitConverter.ToSingle(b, o + OffCs);
        if (!(cs >= 0.05f && cs <= 8f)) return WHY_MAIN;
        for (int i = 0; i < EnumOffs.Count; i++)
            if (!EnumSets[i].ContainsKey(U32(b, o + EnumOffs[i]))) return WHY_ENUM;
        int trues = 0;
        for (int i = 0; i < Bools.Count; i++)
        {
            byte v = b[o + Bools[i]];
            if (v > 1) return WHY_FLAG;
            trues += v;
        }
        if (trues < MinTrue) return WHY_FLAG;
        // Settings floats are ordinary numbers. NaN or a tiny non-zero value means the bytes belong to something
        // else (typically half of a pointer).
        for (int i = 0; i < Floats.Count; i++)
        {
            float f = BitConverter.ToSingle(b, o + Floats[i]);
            if (float.IsNaN(f)) return WHY_FLOAT;
            float a = Math.Abs(f);
            if (a != 0f && a < 1e-20f) return WHY_FLOAT;
        }
        return WHY_OK;
    }
    public bool Check(byte[] b, int o, Proc p) { return Why(b, o, p) == WHY_OK; }

    // The same layout, checked only within the first 'size' bytes of the object.
    public WrsLayout Window(int size)
    {
        WrsLayout c = (WrsLayout)MemberwiseClone();
        c.Size = size;
        c.Bools = new List<int>();
        foreach (int x in Bools) if (x + 1 <= size) c.Bools.Add(x);
        c.Floats = new List<int>();
        foreach (int x in Floats) if (x + 4 <= size) c.Floats.Add(x);
        c.EnumOffs = new List<int>();
        c.EnumSets = new List<Dictionary<uint, bool>>();
        for (int i = 0; i < EnumOffs.Count; i++)
            if (EnumOffs[i] + 4 <= size) { c.EnumOffs.Add(EnumOffs[i]); c.EnumSets.Add(EnumSets[i]); }
        c.MinTrue = 0;
        c.Strong = Strong && c.Bools.Count + c.EnumOffs.Count >= 8;
        c.Full = this;
        return c;
    }
    // Same offsets and values, without the per-field checks (used when the field data does not fit the objects).
    public WrsLayout Basic()
    {
        WrsLayout c = (WrsLayout)MemberwiseClone();
        c.Bools = new List<int>(); c.Floats = new List<int>(); c.EnumOffs = new List<int>(); c.EnumSets = new List<Dictionary<uint, bool>>();
        c.MinTrue = 0; c.Strong = false; c.Full = null;
        c.Source = Source + ", basic checks";
        return c;
    }
    public string Counts() { return Bools.Count + " flags, " + EnumOffs.Count + " enum fields, " + Floats.Count + " floats in 0x" + Size.ToString("X") + " bytes"; }

    // Values of the FC27.exe build with PE timestamp 0x6AD922D1 (used only if the engine lookup fails).
    public static WrsLayout Builtin(ulong imgBase)
    {
        WrsLayout L = new WrsLayout();
        L.OffCs = 0xB0; L.OffUp = 0xCC; L.OffFg = 0x170; L.OffN = 0x1AC; L.OffFs = 0x1C0; L.Size = 0x2E8;
        L.Up[1] = 0x400; L.Up[2] = 0x401; L.Up[3] = 0x402; L.Up[4] = 0x403; L.Up[5] = 0x406;
        L.UpFsr[1] = 0x800; L.UpFsr[2] = 0x801; L.UpFsr[3] = 0x802; L.UpFsr[4] = 0x803; L.UpFsr[5] = 0x806;
        L.HasFsr = true;
        uint[] all = { 0x0, 0x1, 0x2, 0x3, 0x4, 0x200, 0x201, 0x202, 0x203, 0x204, 0x300, 0x301, 0x302, 0x303, 0x306, 0x307,
                       0x400, 0x401, 0x402, 0x403, 0x406, 0x500, 0x501, 0x502, 0x503, 0x504, 0x505, 0x506, 0x600, 0x601, 0x602, 0x603, 0x604,
                       0x700, 0x701, 0x702, 0x703, 0x706, 0x800, 0x801, 0x802, 0x803, 0x804, 0x806, 0x900, 0x901, 0x902, 0x903, 0x906,
                       0xA00, 0xA01, 0xA02, 0xA03, 0xA04 };
        foreach (uint v in all) L.UpSet[v] = true;
        for (uint v = 0; v <= 2; v++) { L.FsSet[v] = true; L.FgSet[v] = true; }
        L.FsNone = 0; L.FsTu = 2; L.FgOff = 0;
        L.MinTrue = 0;
        L.NameStr = imgBase + 0xAEA2870UL;
        L.Source = "built-in values";
        return L;
    }
}

// ---------------------------------------------------------------------------------------------
// Reads the Frostbite type information (class / field / enum descriptions) from the game image.
// Layout of a member info block: +0x00 name, +0x32 u16 count, +0x38 super TypeInfo, +0x40 entry array;
// field entries (0x20 bytes): +0x00 name, +0x10 u32 offset, +0x18 type; enum entries: +0x18 u32 value.
// ---------------------------------------------------------------------------------------------
public class Resolver
{
    class Sec { public string Name; public ulong Va; public uint Size; public uint Flags; public byte[] Data; public bool Code { get { return (Flags & 0x20) != 0; } } }
    class Field { public string Name; public uint Offset; public ulong Type; public string TypeName; }

    Proc P;
    Action<string> Log;
    List<Sec> Secs = new List<Sec>();

    public Resolver(Proc p, Action<string> log) { P = p; Log = log; }

    static ulong U64(byte[] b, int o) { return BitConverter.ToUInt64(b, o); }
    static uint U32(byte[] b, int o) { return BitConverter.ToUInt32(b, o); }

    byte[] ReadBig(ulong addr, uint size)
    {
        byte[] all = new byte[size];
        const int CH = 1 << 20;
        byte[] part = new byte[CH];
        for (uint off = 0; off < size; off += CH)
        {
            int n = (int)Math.Min(CH, size - off);
            int g = P.ReadInto(addr + off, part, n);
            if (g > 0) Buffer.BlockCopy(part, 0, all, (int)off, Math.Min(g, n));
        }
        return all;
    }
    Sec SecOf(ulong a) { foreach (Sec s in Secs) if (a >= s.Va && a < s.Va + s.Size) return s; return null; }
    byte[] ReadAt(ulong addr, int n)
    {
        Sec s = SecOf(addr);
        if (s != null && s.Data != null && addr + (ulong)n <= s.Va + s.Size)
        {
            byte[] r = new byte[n];
            Buffer.BlockCopy(s.Data, (int)(addr - s.Va), r, 0, n);
            return r;
        }
        return P.Read(addr, n);
    }
    ulong Q(ulong addr) { byte[] b = ReadAt(addr, 8); return b == null ? 0 : U64(b, 0); }
    string CStr(ulong addr)
    {
        if (!P.InImage(addr)) return null;
        Sec s = SecOf(addr);
        if (s == null || s.Data == null) return null;
        int o = (int)(addr - s.Va);
        StringBuilder sb = new StringBuilder();
        for (int i = o; i < s.Data.Length && i < o + 160; i++)
        {
            byte c = s.Data[i];
            if (c == 0) return sb.Length > 0 ? sb.ToString() : null;
            if (c < 0x20 || c > 0x7E) return null;
            sb.Append((char)c);
        }
        return null;
    }
    List<ulong> FindString(string str)
    {
        List<ulong> res = new List<ulong>();
        byte[] pat = Encoding.ASCII.GetBytes("\0" + str + "\0");
        foreach (Sec s in Secs)
        {
            if (s.Code || s.Data == null) continue;
            byte[] d = s.Data;
            for (int i = 0; i + pat.Length <= d.Length; i++)
            {
                if (d[i + 1] != pat[1]) continue;
                bool ok = true;
                for (int j = 0; j < pat.Length; j++) if (d[i + j] != pat[j]) { ok = false; break; }
                if (ok) res.Add(s.Va + (ulong)i + 1);
            }
        }
        return res;
    }
    List<ulong> FindPointersTo(ulong target)
    {
        List<ulong> res = new List<ulong>();
        foreach (Sec s in Secs)
        {
            if (s.Code || s.Data == null) continue;
            byte[] d = s.Data;
            for (int i = 0; i + 8 <= d.Length; i += 8) if (U64(d, i) == target) res.Add(s.Va + (ulong)i);
        }
        return res;
    }
    bool LooksLikeInfo(ulong d, out ushort count, out ulong arr)
    {
        count = 0; arr = 0;
        byte[] h = ReadAt(d, 0x48);
        if (h == null) return false;
        count = BitConverter.ToUInt16(h, 0x32);
        arr = U64(h, 0x40);
        if (count == 0 || count > 4000 || !P.InImage(arr)) return false;
        return CStr(Q(arr)) != null;
    }
    string TypeName(ulong typeInfo)
    {
        if (!P.InImage(typeInfo)) return "?";
        string n = CStr(Q(Q(typeInfo)));
        return n ?? "?";
    }

    // Enum: first info block named 'name' whose entries carry the prefix 'name_'.
    Dictionary<string, uint> LoadEnum(string name)
    {
        foreach (ulong sa in FindString(name))
            foreach (ulong loc in FindPointersTo(sa))
            {
                ushort cnt; ulong arr;
                if (!LooksLikeInfo(loc, out cnt, out arr)) continue;
                Dictionary<string, uint> r = new Dictionary<string, uint>();
                bool prefixed = false;
                for (int i = 0; i < cnt; i++)
                {
                    byte[] e = ReadAt(arr + (ulong)(i * 0x20), 0x20);
                    if (e == null) break;
                    string n = CStr(U64(e, 0));
                    if (n == null) continue;
                    r[n] = U32(e, 0x18);
                    if (n.StartsWith(name + "_", StringComparison.Ordinal)) prefixed = true;
                }
                if (prefixed) return r;
            }
        return new Dictionary<string, uint>();
    }

    // All fields of a class including its base classes, starting at a class info block.
    List<Field> CollectFields(ulong d)
    {
        List<Field> res = new List<Field>();
        int depth = 0;
        while (d != 0 && depth < 12)
        {
            ushort cnt; ulong arr;
            if (LooksLikeInfo(d, out cnt, out arr))
            {
                for (int i = 0; i < cnt; i++)
                {
                    byte[] e = ReadAt(arr + (ulong)(i * 0x20), 0x20);
                    if (e == null) break;
                    Field f = new Field();
                    f.Name = CStr(U64(e, 0)) ?? "?";
                    f.Offset = U32(e, 0x10);
                    f.Type = U64(e, 0x18);
                    f.TypeName = TypeName(f.Type);
                    res.Add(f);
                }
            }
            ulong sup = Q(d + 0x38);
            if (!P.InImage(sup)) break;
            ulong sd = Q(sup);
            if (sd == d || !P.InImage(sd)) break;
            d = sd;
            depth++;
        }
        return res;
    }
    bool FieldEntryOk(ulong e)
    {
        byte[] b = ReadAt(e, 0x20);
        if (b == null) return false;
        if (CStr(U64(b, 0)) == null) return false;
        ulong t = U64(b, 0x18);
        if (!P.InImage(t) || TypeName(t) == "?") return false;
        return U32(b, 0x10) < 0x10000;
    }
    // Fallback: the field array that contains the entry 'fieldName' of type 'typeName'.
    List<Field> FieldsAround(string fieldName, string typeName)
    {
        List<Field> res = new List<Field>();
        foreach (ulong sa in FindString(fieldName))
            foreach (ulong e in FindPointersTo(sa))
            {
                if (!FieldEntryOk(e) || TypeName(Q(e + 0x18)) != typeName) continue;
                ulong st = e;
                while (FieldEntryOk(st - 0x20)) st -= 0x20;
                for (ulong x = st; FieldEntryOk(x); x += 0x20)
                {
                    byte[] b = ReadAt(x, 0x20);
                    Field f = new Field();
                    f.Name = CStr(U64(b, 0)); f.Offset = U32(b, 0x10); f.Type = U64(b, 0x18); f.TypeName = TypeName(f.Type);
                    res.Add(f);
                }
                return res;
            }
        return res;
    }
    static Field Get(List<Field> fs, string n) { foreach (Field f in fs) if (f.Name == n) return f; return null; }
    static bool HasKeyFields(List<Field> fs)
    {
        return Get(fs, "TemporalUpsampleMode") != null && Get(fs, "FrameSynthesisMode") != null && Get(fs, "NvidiaFrameGenerationMode") != null
            && Get(fs, "NvidiaFrameGenerationNumGeneratedFrames") != null && Get(fs, "TemporalUpsampleCustomScale") != null;
    }

    public WrsLayout Resolve()
    {
        Stopwatch sw = Stopwatch.StartNew();
        byte[] hdr = P.Read(P.Base, 0x1000);
        if (hdr == null) return null;
        int pe = BitConverter.ToInt32(hdr, 0x3C);
        if (pe <= 0 || pe > 0xE00) return null;
        int nsec = BitConverter.ToUInt16(hdr, pe + 6);
        int opt = BitConverter.ToUInt16(hdr, pe + 20);
        for (int i = 0; i < nsec; i++)
        {
            int o = pe + 24 + opt + i * 40;
            if (o + 40 > hdr.Length) break;
            Sec s = new Sec();
            s.Name = Encoding.ASCII.GetString(hdr, o, 8).TrimEnd('\0');
            s.Size = BitConverter.ToUInt32(hdr, o + 8);
            s.Va = P.Base + BitConverter.ToUInt32(hdr, o + 12);
            s.Flags = BitConverter.ToUInt32(hdr, o + 36);
            if (!s.Code && s.Size > 0 && s.Size < 0x10000000) s.Data = ReadBig(s.Va, s.Size);
            Secs.Add(s);
        }
        try
        {
            Dictionary<string, Dictionary<string, uint>> enums = new Dictionary<string, Dictionary<string, uint>>();
            string[] enumNames = { "TemporalUpsampleMode", "FrameSynthesisMode", "NvidiaFrameGenerationMode", "NvidiaReflexMode", "PostProcessAAMode",
                                   "AmdFrameGenerationMode", "IntelXeFGMode", "TemporalUpsampleAutoReactiveMethod" };
            foreach (string en in enumNames) enums[en] = LoadEnum(en);

            Dictionary<string, uint> eUp = enums["TemporalUpsampleMode"], eFs = enums["FrameSynthesisMode"], eFg = enums["NvidiaFrameGenerationMode"];
            string[] upNames = { "", "TemporalUpsampleMode_DLSS_Ultra_Performance", "TemporalUpsampleMode_DLSS_Performance", "TemporalUpsampleMode_DLSS_Balanced",
                                 "TemporalUpsampleMode_DLSS_Quality", "TemporalUpsampleMode_DLSS_DLAA" };
            WrsLayout L = new WrsLayout();
            for (int i = 1; i <= 5; i++)
            {
                uint v;
                if (!eUp.TryGetValue(upNames[i], out v)) { Log("Engine: enum value " + upNames[i] + " missing"); return null; }
                L.Up[i] = v;
            }
            string[] fsrNames = { "", "TemporalUpsampleMode_Fsr3Upsampler_Ultra_Performance", "TemporalUpsampleMode_Fsr3Upsampler_Performance",
                                  "TemporalUpsampleMode_Fsr3Upsampler_Balanced", "TemporalUpsampleMode_Fsr3Upsampler_Quality", "TemporalUpsampleMode_Fsr3Upsampler_Native" };
            L.HasFsr = true;
            for (int i = 1; i <= 5; i++)
            {
                uint v;
                if (eUp.TryGetValue(fsrNames[i], out v)) L.UpFsr[i] = v;
                else L.HasFsr = false;
            }
            if (!L.HasFsr) Log("Engine: FSR 3 values missing - FSR 3 is not available in this game version");
            if (!eFs.TryGetValue("FrameSynthesisMode_None", out L.FsNone) || !eFs.TryGetValue("FrameSynthesisMode_TemporalUpsample", out L.FsTu)
                || !eFg.TryGetValue("NvidiaFrameGenerationMode_Disabled", out L.FgOff))
            { Log("Engine: FrameSynthesis / FrameGeneration values missing"); return null; }
            foreach (uint v in eUp.Values) L.UpSet[v] = true;
            foreach (uint v in eFs.Values) L.FsSet[v] = true;
            foreach (uint v in eFg.Values) L.FgSet[v] = true;

            List<Field> fields = null;
            List<ulong> nameStrs = FindString("WorldRenderSettings");
            foreach (ulong sa in nameStrs)
            {
                foreach (ulong loc in FindPointersTo(sa))
                {
                    List<Field> fs = CollectFields(loc);
                    if (HasKeyFields(fs)) { fields = fs; break; }
                }
                if (fields != null) break;
            }
            if (fields == null)
            {
                List<Field> fs = FieldsAround("TemporalUpsampleMode", "TemporalUpsampleMode");
                if (HasKeyFields(fs)) fields = fs;
            }
            if (fields == null) { Log("Engine: WorldRenderSettings fields not found"); return null; }
            if (nameStrs.Count > 0) L.NameStr = nameStrs[0];

            Field fUp = Get(fields, "TemporalUpsampleMode"), fFs = Get(fields, "FrameSynthesisMode"), fFg = Get(fields, "NvidiaFrameGenerationMode");
            Field fN = Get(fields, "NvidiaFrameGenerationNumGeneratedFrames"), fCs = Get(fields, "TemporalUpsampleCustomScale");
            L.OffUp = (int)fUp.Offset; L.OffFs = (int)fFs.Offset; L.OffFg = (int)fFg.Offset; L.OffN = (int)fN.Offset; L.OffCs = (int)fCs.Offset;

            Field fb = Get(fields, "TemporalUpsampleAutoReactiveEnable");
            string boolName = fb != null ? fb.TypeName : "Boolean";
            uint maxEnd = 0;
            foreach (Field f in fields) if (f.Offset < 0x4000 && f.Offset + 8 > maxEnd) maxEnd = f.Offset + 8;
            L.Size = (int)((maxEnd + 7) & ~7u);
            Dictionary<uint, bool> seen = new Dictionary<uint, bool>();
            foreach (Field f in fields)
            {
                if (f.Offset + 4 > (uint)L.Size || seen.ContainsKey(f.Offset)) continue;
                seen[f.Offset] = true;
                if (f.TypeName == boolName) L.Bools.Add((int)f.Offset);
                else if (f.TypeName == "Float32") L.Floats.Add((int)f.Offset);
                else if (enums.ContainsKey(f.TypeName) && enums[f.TypeName].Count > 0)
                {
                    Dictionary<uint, bool> set = new Dictionary<uint, bool>();
                    foreach (uint v in enums[f.TypeName].Values) set[v] = true;
                    L.EnumOffs.Add((int)f.Offset);
                    L.EnumSets.Add(set);
                }
            }
            L.MinTrue = L.Bools.Count >= 40 ? 8 : 0;
            L.Strong = L.Bools.Count + L.EnumOffs.Count >= 8;
            L.Source = "Engine (" + fields.Count + " fields, " + L.Bools.Count + " flags, " + L.EnumOffs.Count + " enum fields, " + L.Floats.Count + " floats, " + sw.ElapsedMilliseconds + " ms)";
            int[] offs = { L.OffUp, L.OffFs, L.OffFg, L.OffN, L.OffCs };
            foreach (int o in offs) if (o <= 8 || o + 4 > L.Size) { Log("Engine: implausible offset 0x" + o.ToString("X")); return null; }
            return L;
        }
        finally
        {
            foreach (Sec s in Secs) s.Data = null;
        }
    }
}

// ---------------------------------------------------------------------------------------------
// Settings file (keys, OSD)
// ---------------------------------------------------------------------------------------------
public class Config
{
    public uint Mods = 0x2;
    public string ModText = "Ctrl";
    public uint[] Vk = { 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x7B };    // F5 .. F12
    public string Gpu = "auto";
    public bool Osd = true;
    public int OsdX = 24, OsdY = 24;
    public bool NoDefaults = false;
    static readonly string[] KeyIds = { "key_toggle", "key_ultraperformance", "key_performance", "key_balanced", "key_quality", "key_native", "key_status", "key_upscaler" };
    static readonly string[] KeyIdsDe = { "taste_anaus", "taste_ultraperformance", "taste_performance", "taste_balanced", "taste_quality", "taste_dlaa", "taste_status", "taste_upscaler" };

    public string KeyText(int i)
    {
        if (Vk[i] == 0) return "-";
        string k = ((Keys)Vk[i]).ToString();
        if (k.Length == 2 && k[0] == 'D' && char.IsDigit(k[1])) k = k.Substring(1);
        return (ModText.Length > 0 ? ModText + "+" : "") + k;
    }

    public static Config Load(string path, Action<string> log)
    {
        Config c = new Config();
        try
        {
            if (!File.Exists(path)) { File.WriteAllText(path, DefaultText.Replace("\n", "\r\n"), Encoding.UTF8); return c; }
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#' || line[0] == '[') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                string val = line.Substring(eq + 1).Trim();
                int sc = val.IndexOf(';');
                if (sc >= 0) val = val.Substring(0, sc).Trim();
                int iv;
                if (key == "modifier" || key == "modifikator") ParseMods(c, val, log);
                else if (key == "osd") c.Osd = val != "0";
                else if (key == "osd_x") { if (int.TryParse(val, out iv)) c.OsdX = iv; }
                else if (key == "osd_y") { if (int.TryParse(val, out iv)) c.OsdY = iv; }
                else if (key == "nodefaults" || key == "ohnevorgaben") c.NoDefaults = val == "1";
                else if (key == "gpu" || key == "grafikkarte")
                {
                    string g = val.ToLowerInvariant();
                    if (g == "andere") g = "other";
                    if (g == "nvidia" || g == "amd" || g == "intel" || g == "other" || g == "auto") c.Gpu = g;
                    else log("Unknown GPU setting '" + val + "' - using Auto");
                }
                else
                {
                    int idx = Array.IndexOf(KeyIds, key);
                    if (idx < 0) idx = Array.IndexOf(KeyIdsDe, key);
                    if (idx >= 0) c.Vk[idx] = ParseKey(val, c.Vk[idx], log);
                }
            }
        }
        catch (Exception ex) { log("Settings file: " + ex.Message); }
        return c;
    }
    static uint ParseKey(string s, uint def, Action<string> log)
    {
        string t = s.Trim();
        if (t.Length == 0 || t.Equals("None", StringComparison.OrdinalIgnoreCase) || t.Equals("Keine", StringComparison.OrdinalIgnoreCase)) return 0;
        if (t.Length == 1 && char.IsDigit(t[0])) t = "D" + t;
        try
        {
            Keys k = (Keys)Enum.Parse(typeof(Keys), t, true);
            uint v = (uint)(k & Keys.KeyCode);
            if (v != 0) return v;
        }
        catch { }
        log("Unknown key '" + s + "' - using default");
        return def;
    }
    static void ParseMods(Config c, string s, Action<string> log)
    {
        uint m = 0;
        List<string> txt = new List<string>();
        foreach (string part in s.Split('+'))
        {
            string p = part.Trim().ToLowerInvariant();
            if (p == "ctrl" || p == "control" || p == "strg") { m |= 2; txt.Add("Ctrl"); }
            else if (p == "alt") { m |= 1; txt.Add("Alt"); }
            else if (p == "shift" || p == "umschalt") { m |= 4; txt.Add("Shift"); }
            else if (p == "win") { m |= 8; txt.Add("Win"); }
            else if (p == "none" || p == "keine" || p.Length == 0) { }
            else log("Unknown modifier '" + part + "'");
        }
        c.Mods = m;
        c.ModText = string.Join("+", txt.ToArray());
    }

    const string DefaultText =
        "; FC 27 DLSS Switch v1.2 - settings\n" +
        "; Changes take effect after restarting the switch (tray icon > Exit, then start it again).\n" +
        ";\n" +
        "; Modifier: Ctrl, Alt, Shift, Win or None (can be combined with +, e.g. Ctrl+Alt)\n" +
        "; Keys: F1-F12, A-Z, 0-9, NumPad0-NumPad9, Insert, Home, PageUp ... or None (key disabled)\n" +
        "Modifier=Ctrl\n" +
        "Key_Toggle=F5\n" +
        "Key_UltraPerformance=F6\n" +
        "Key_Performance=F7\n" +
        "Key_Balanced=F8\n" +
        "Key_Quality=F9\n" +
        "Key_Native=F10\n" +
        "Key_Status=F11\n" +
        "; Switch between DLSS and FSR 3 (NVIDIA graphics cards only)\n" +
        "Key_Upscaler=F12\n" +
        "\n" +
        "; Graphics card: Auto (detected automatically), NVIDIA, AMD or Intel.\n" +
        "; NVIDIA gets DLSS and FSR 3, AMD and Intel get FSR 3 only.\n" +
        "GPU=Auto\n" +
        "\n" +
        "; On-screen display when switching: 1 = on, 0 = off\n" +
        "OSD=1\n" +
        "; Position of the display in pixels (top left = 24, 24)\n" +
        "OSD_X=24\n" +
        "OSD_Y=24\n" +
        "\n" +
        "; Testing only: 1 = ignore saved and built-in memory addresses and search everything again\n" +
        "NoDefaults=0\n";
}

// ---------------------------------------------------------------------------------------------
// The switch: tray icon, hotkeys, on-screen display and the background worker
// ---------------------------------------------------------------------------------------------
public class DlssSwitch : Form
{
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    const uint BUILTIN_STAMP = 0x6AD922D1;
    static readonly ulong[] BuiltinVtRva = { 0xAEA2A10UL, 0xAEA29E0UL, 0x96CA1C0UL };
    const string WRS_NAME = "WorldRenderSettings";
    const int MAX_TARGETS = 256;         // FC 27 keeps 60+ copies of the settings at times; this is only a sanity limit
    const int SETTLE_MS = 500;           // a found object must stay unchanged this long before it is written to

    // Upscaler families: 0 = NVIDIA DLSS (through Streamline), 1 = AMD FSR 3 (built into the engine, runs on every GPU).
    const int FAM_DLSS = 0, FAM_FSR = 1;
    static readonly string[] DlssNames = { "OFF (game's TAA)", "DLSS Ultra Performance", "DLSS Performance", "DLSS Balanced", "DLSS Quality", "DLAA" };
    static readonly string[] FsrNames = { "OFF (game's TAA)", "FSR 3 Ultra Performance", "FSR 3 Performance", "FSR 3 Balanced", "FSR 3 Quality", "FSR 3 Native AA" };
    static readonly string[] ScaleTxt = { "", "internal 33 %", "internal 50 %", "internal 58 %", "internal 67 %", "internal 100 %" };
    static readonly int[] HotkeyIds = { 100, 101, 102, 103, 104, 105, 110, 106 };     // same order as Config.Vk

    volatile int mode = 5;
    volatile int lastOn = 5;
    volatile int fam = FAM_DLSS;
    volatile bool closing, rescanRequested;
    volatile string state = "waiting for FC 27";
    bool nvidia = true, nvidiaConfirmed = false;
    string gpuText = "unknown";
    string dir, statePath, logPath, cachePath, iniPath;
    Config cfg;
    Label label;
    System.Windows.Forms.Timer hideTimer;
    NotifyIcon tray;
    MenuItem offItem;
    MenuItem[] dlssItems = new MenuItem[6];
    MenuItem[] fsrItems = new MenuItem[6];
    readonly object setLock = new object();
    readonly object logLock = new object();
    Dictionary<ulong, ulong> targets = new Dictionary<ulong, ulong>();     // object address -> its vtable (verified, written to)
    Dictionary<ulong, Cand> pending = new Dictionary<ulong, Cand>();       // found, waiting for the second look
    Dictionary<ulong, int> strikes = new Dictionary<ulong, int>();         // addresses that failed the second look
    Dictionary<ulong, bool> vtables = new Dictionary<ulong, bool>();
    List<ulong[]> regions = new List<ulong[]>();
    readonly Stopwatch clock = Stopwatch.StartNew();
    volatile bool typesConfirmed, relaxRequested;
    volatile int relaxVotes;

    class Cand { public ulong Vt; public byte[] Snap; public long Seen; }

    static string ModeName(int f, int m) { return f == FAM_FSR ? FsrNames[m] : DlssNames[m]; }

    public DlssSwitch(string dir)
    {
        this.dir = dir;
        statePath = Path.Combine(dir, "FC27_DLSS_Switch_mode.txt");
        logPath = Path.Combine(dir, "FC27_DLSS_Switch_log.txt");
        cachePath = Path.Combine(dir, "FC27_DLSS_Switch_cache.txt");
        iniPath = Path.Combine(dir, "FC27_DLSS_Switch.ini");
        try { File.WriteAllText(logPath, "FC 27 DLSS Switch v1.2  " + DateTime.Now.ToString("s") + Environment.NewLine); } catch { }
        cfg = Config.Load(iniPath, Log);
        DetectGpu();
        LoadState();

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(cfg.OsdX, cfg.OsdY);
        BackColor = Color.FromArgb(18, 18, 18);
        Opacity = 0.88;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(14, 10, 14, 10);
        Text = "FC 27 DLSS Switch";
        label = new Label();
        label.AutoSize = true;
        label.Location = new Point(14, 10);
        label.ForeColor = Color.FromArgb(118, 185, 0);
        label.Font = new Font("Segoe UI", 14f, FontStyle.Bold);
        Controls.Add(label);

        hideTimer = new System.Windows.Forms.Timer();
        hideTimer.Tick += delegate { hideTimer.Stop(); Visible = false; };

        tray = new NotifyIcon();
        try { tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { tray.Icon = SystemIcons.Application; }
        tray.Text = "FC 27 DLSS Switch";
        ContextMenu cm = new ContextMenu();
        MenuItem title = new MenuItem("FC 27 DLSS Switch v1.2");
        title.Enabled = false;
        cm.MenuItems.Add(title);
        MenuItem hint = new MenuItem("Level: " + cfg.KeyText(1) + " to " + cfg.KeyText(5) + ",  on/off: " + cfg.KeyText(0));
        hint.Enabled = false;
        cm.MenuItems.Add(hint);
        cm.MenuItems.Add("-");
        offItem = new MenuItem("Upscaling off (game's TAA)");
        offItem.RadioCheck = true;
        offItem.Click += delegate { SetMode(fam, 0); };
        cm.MenuItems.Add(offItem);
        if (nvidia)
        {
            cm.MenuItems.Add("-");
            for (int i = 1; i <= 5; i++)
            {
                int idx = i;
                MenuItem mi = new MenuItem(DlssNames[i]);
                mi.RadioCheck = true;
                mi.Click += delegate { SetMode(FAM_DLSS, idx); };
                dlssItems[i] = mi;
                cm.MenuItems.Add(mi);
            }
        }
        cm.MenuItems.Add("-");
        for (int i = 1; i <= 5; i++)
        {
            int idx = i;
            MenuItem mi = new MenuItem(FsrNames[i]);
            mi.RadioCheck = true;
            mi.Click += delegate { SetMode(FAM_FSR, idx); };
            fsrItems[i] = mi;
            cm.MenuItems.Add(mi);
        }
        cm.MenuItems.Add("-");
        if (nvidia) cm.MenuItems.Add("Switch DLSS / FSR 3\t" + cfg.KeyText(7), delegate { ToggleFamily(); });
        cm.MenuItems.Add("Show status\t" + cfg.KeyText(6), delegate { ShowStatus(true); });
        cm.MenuItems.Add("Search settings again", delegate { rescanRequested = true; Osd("Searching the graphics settings again ...", 2500, true); });
        cm.MenuItems.Add("Change keys (settings file)", delegate { OpenIni(); });
        cm.MenuItems.Add("-");
        cm.MenuItems.Add("Exit", delegate { closing = true; Close(); });
        tray.ContextMenu = cm;
        tray.DoubleClick += delegate { ShowStatus(true); };
        tray.Visible = true;
        UpdateMenu();
    }

    // DLSS needs an NVIDIA GPU; FSR 3 works everywhere. Unknown hardware gets both options.
    void DetectGpu()
    {
        int nv = -1;
        List<string> names = new List<string>();
        try
        {
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT Name, PNPDeviceID FROM Win32_VideoController"))
            using (ManagementObjectCollection col = searcher.Get())
            {
                nv = 0;
                foreach (ManagementObject o in col)
                {
                    string n = Convert.ToString(o["Name"]);
                    string id = Convert.ToString(o["PNPDeviceID"]);
                    if (!string.IsNullOrEmpty(n)) names.Add(n);
                    if ((id ?? "").ToUpperInvariant().Contains("VEN_10DE") || (n ?? "").ToUpperInvariant().Contains("NVIDIA")) nv = 1;
                    o.Dispose();
                }
            }
        }
        catch (Exception ex) { Log("Graphics card not detected: " + ex.Message); nv = -1; }
        gpuText = names.Count > 0 ? string.Join(", ", names.ToArray()) : "unknown";
        if (cfg.Gpu == "nvidia") nv = 1;
        else if (cfg.Gpu == "amd" || cfg.Gpu == "intel" || cfg.Gpu == "other") nv = 0;
        nvidia = nv != 0;
        nvidiaConfirmed = nv == 1;
        Log("Graphics card: " + gpuText + (cfg.Gpu != "auto" ? " (setting: " + cfg.Gpu + ")" : "") + " -> " + (nvidia ? "DLSS and FSR 3" : "FSR 3 only"));
    }

    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x8000000 | 0x8;   // layered, click-through, tool window, no-activate, topmost
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        string[] what = { "On/Off", "Ultra Performance", "Performance", "Balanced", "Quality", "DLAA / Native AA", "Status", "Switch DLSS/FSR" };
        for (int i = 0; i < HotkeyIds.Length; i++)
        {
            if (cfg.Vk[i] == 0) continue;
            if (HotkeyIds[i] == 106 && !nvidia) continue;
            if (!RegisterHotKey(Handle, HotkeyIds[i], cfg.Mods | 0x4000, cfg.Vk[i]))
                Log("Key " + cfg.KeyText(i) + " (" + what[i] + ") is already used by another program");
        }
        Log("Start OK - " + ModeName(fam, mode) + ", keys " + cfg.KeyText(0) + " .. " + cfg.KeyText(6) + (nvidia ? ", " + cfg.KeyText(7) : ""));
    }
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        string keys = cfg.KeyText(0) + " on/off  ·  " + cfg.KeyText(1) + " to " + cfg.KeyText(5) + " Ultra Performance to Native";
        if (nvidia) keys += "\n" + cfg.KeyText(7) + " switch DLSS / FSR 3  ·  " + cfg.KeyText(6) + " status";
        else keys += "  ·  " + cfg.KeyText(6) + " status\n(no NVIDIA graphics card: FSR 3)";
        Osd("FC 27 DLSS Switch active  –  " + ModeName(fam, mode) + "\n" + keys + "\n" + state, 8000, true);
        Thread w = new Thread(Worker);
        w.IsBackground = true;
        w.Start();
    }
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        closing = true;
        foreach (int id in HotkeyIds) UnregisterHotKey(Handle, id);
        tray.Visible = false;
        base.OnFormClosing(e);
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x312)
        {
            int id = m.WParam.ToInt32();
            if (id == 100) SetMode(fam, mode != 0 ? 0 : (lastOn >= 1 && lastOn <= 5 ? lastOn : 5));
            else if (id >= 101 && id <= 105) SetMode(fam, id - 100);
            else if (id == 106) ToggleFamily();
            else if (id == 110) { rescanRequested = true; ShowStatus(true); }
        }
        base.WndProc(ref m);
    }

    void ToggleFamily()
    {
        if (!nvidia) { Osd("Without an NVIDIA graphics card only FSR 3 is available.", 3000, true); return; }
        int m = mode != 0 ? mode : (lastOn >= 1 && lastOn <= 5 ? lastOn : 5);
        SetMode(fam == FAM_DLSS ? FAM_FSR : FAM_DLSS, m);
    }
    void SetMode(int f, int m)
    {
        if (m < 0 || m > 5) return;
        if (f != FAM_FSR) f = FAM_DLSS;
        if (!nvidia) f = FAM_FSR;
        if (m > 0) lastOn = m;
        fam = f;
        mode = m;
        SaveState();
        Log("Mode -> " + ModeName(f, m));
        UpdateMenu();
        ShowStatus(false);
    }
    void UpdateMenu()
    {
        if (offItem != null) offItem.Checked = mode == 0;
        for (int i = 1; i <= 5; i++)
        {
            if (dlssItems[i] != null) dlssItems[i].Checked = mode == i && fam == FAM_DLSS;
            if (fsrItems[i] != null) fsrItems[i].Checked = mode == i && fam == FAM_FSR;
        }
        try { tray.Text = "FC 27: " + (mode == 0 ? "Upscaling off" : ModeName(fam, mode)); } catch { }
    }
    void LoadState()
    {
        int f = -1;
        try
        {
            string[] p = File.ReadAllText(statePath).Trim().Split(' ');
            int a, b, c;
            if (p.Length > 0 && int.TryParse(p[0], out a) && a >= 0 && a <= 5) mode = a;
            if (p.Length > 1 && int.TryParse(p[1], out b) && b >= 1 && b <= 5) lastOn = b;
            if (p.Length > 2 && int.TryParse(p[2], out c) && (c == FAM_DLSS || c == FAM_FSR)) f = c;
            if (mode > 0) lastOn = mode;
        }
        catch { }
        if (f < 0) f = nvidiaConfirmed ? FAM_DLSS : FAM_FSR;
        if (!nvidia) f = FAM_FSR;
        fam = f;
    }
    void SaveState() { try { File.WriteAllText(statePath, mode + " " + lastOn + " " + fam); } catch { } }
    void OpenIni()
    {
        try { Process.Start("notepad.exe", "\"" + iniPath + "\""); }
        catch (Exception ex) { Log("Editor: " + ex.Message); }
    }

    void ShowStatus(bool detail)
    {
        int n;
        lock (setLock) n = targets.Count;
        int md = mode;
        string s = "Upscaler: " + ModeName(fam, md) + (md > 0 ? "  (" + ScaleTxt[md] + ")" : "");
        if (n == 0) s += "\n" + state;
        else if (detail) s += "\n" + state + "  ·  " + n + " settings objects";
        Osd(s, detail ? 4000 : 2500, detail);
    }
    void Osd(string text, int ms, bool force)
    {
        if (closing) return;
        if (!cfg.Osd && !force) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(new Action<string, int, bool>(Osd), text, ms, force); } catch { }
            return;
        }
        label.Text = text;
        Visible = true;
        hideTimer.Stop();
        hideTimer.Interval = ms;
        hideTimer.Start();
    }
    void Log(string s)
    {
        lock (logLock)
        {
            try { File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + Environment.NewLine); } catch { }
        }
    }

    // ---------------- cache of the object types found (per game build) ----------------
    // Only the types are remembered. Object addresses from an earlier game session are never reused:
    // in a new session the same address can belong to something completely different.
    void LoadCache(uint stamp, List<ulong> vtRva)
    {
        try
        {
            if (!File.Exists(cachePath)) return;
            bool match = false;
            foreach (string l in File.ReadAllLines(cachePath))
            {
                int eq = l.IndexOf('=');
                if (eq <= 0) continue;
                string k = l.Substring(0, eq).Trim();
                string v = l.Substring(eq + 1).Trim();
                if (k == "stamp") match = v.Equals(stamp.ToString("X8"), StringComparison.OrdinalIgnoreCase);
                else if (match && k == "vt") ParseHexList(v, vtRva);
            }
        }
        catch (Exception ex) { Log("Cache: " + ex.Message); }
    }
    static void ParseHexList(string v, List<ulong> dst)
    {
        foreach (string s in v.Split(','))
        {
            ulong x;
            if (ulong.TryParse(s.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out x) && x != 0 && !dst.Contains(x)) dst.Add(x);
        }
    }
    void SaveCache(uint stamp, ulong imgBase)
    {
        List<string> vt = new List<string>();
        int n;
        lock (setLock)
        {
            foreach (ulong v in vtables.Keys) if (v > imgBase) vt.Add((v - imgBase).ToString("X"));
            n = targets.Count;
        }
        if (vt.Count == 0 || n == 0) return;
        try
        {
            File.WriteAllText(cachePath, "stamp=" + stamp.ToString("X8") + "\r\nvt=" + string.Join(",", vt.ToArray()) + "\r\n");
        }
        catch { }
    }

    // ---------------- memory side ----------------
    static uint U32(byte[] b, int o) { return BitConverter.ToUInt32(b, o); }
    static ulong U64(byte[] b, int o) { return BitConverter.ToUInt64(b, o); }

    // full = all committed writable memory; otherwise only the regions where objects were found before.
    // Objects found here are only candidates: Promote() looks at them a second time before anything is written.
    int Scan(Proc p, WrsLayout L, bool full, bool structural, int[] stat)
    {
        int found = 0;
        int ov = L.Size + 16;
        const int CH = 8 << 20;
        byte[] buf = new byte[CH + ov];
        Dictionary<ulong, bool> vt;
        lock (setLock) vt = new Dictionary<ulong, bool>(vtables);
        List<ulong[]> work = new List<ulong[]>();
        if (!full) { lock (setLock) work.AddRange(regions); }
        else
        {
            ulong addr = 0x10000;
            Proc.MBI m;
            while (!closing && addr < 0x7FFFFFFF0000UL && p.Query(addr, out m))
            {
                ulong next = m.BaseAddress + m.RegionSize;
                bool rw = (m.Protect & 0x04) != 0 || (m.Protect & 0x08) != 0 || (m.Protect & 0x40) != 0;
                // skip guard pages and uncached / write-combined memory (graphics driver mappings)
                if (m.State == 0x1000 && rw && (m.Protect & 0x700) == 0) work.Add(new ulong[] { m.BaseAddress, m.RegionSize });
                if (next <= addr) break;
                addr = next;
            }
        }
        foreach (ulong[] r in work)
        {
            if (closing || !p.Alive()) break;
            int before = found;
            for (ulong off = 0; off < r[1] && !closing; off += CH)
            {
                int want = (int)Math.Min((ulong)(CH + ov), r[1] - off);
                int g = p.ReadInto(r[0] + off, buf, want);
                if (g <= 0) continue;
                int lim = Math.Min(g, CH);
                for (int i = 0; i + 8 <= lim; i += 8)
                {
                    ulong q = BitConverter.ToUInt64(buf, i);
                    if (structural) { if (!p.InImage(q)) continue; }
                    else if (!vt.ContainsKey(q)) continue;
                    ulong o = r[0] + off + (ulong)i;
                    byte[] src = buf;
                    int at = i;
                    if (i + L.Size > g) { src = p.Read(o, L.Size); at = 0; }
                    int w = L.Why(src, at, p);
                    if (!structural) stat[w]++;
                    if (w != WrsLayout.WHY_OK) continue;
                    lock (setLock)
                    {
                        int k;
                        if (targets.ContainsKey(o) || pending.ContainsKey(o) || (strikes.TryGetValue(o, out k) && k >= 3)) continue;
                        Cand c = new Cand();
                        c.Vt = q;
                        c.Snap = new byte[L.Size];
                        Buffer.BlockCopy(src, at, c.Snap, 0, L.Size);
                        c.Seen = clock.ElapsedMilliseconds;
                        pending[o] = c;
                        found++;
                    }
                }
                Thread.Sleep(0);
            }
            if (full && found > before)
            {
                lock (setLock)
                {
                    bool known = false;
                    foreach (ulong[] x in regions) if (x[0] == r[0]) known = true;
                    if (!known) regions.Add(r);
                }
            }
        }
        return found;
    }

    void ScanJob(Proc p, WrsLayout L, bool full)
    {
        try
        {
            Stopwatch sw = Stopwatch.StartNew();
            bool structural;
            lock (setLock) structural = vtables.Count == 0;
            if (structural && (!L.Strong || L.Bools.Count < 40))
            {
                Log("Structural search not possible (too few known fields)");
                return;
            }
            int[] stat = new int[6];
            int n = Scan(p, L, full, structural, stat);
            if (full)
            {
                StringBuilder s = new StringBuilder();
                s.Append(structural ? "Structural search: " : "Search: ").Append(n).Append(" new candidates in ").Append(sw.ElapsedMilliseconds).Append(" ms");
                if (!structural)
                {
                    int all = 0;
                    foreach (int x in stat) all += x;
                    s.Append(" (").Append(all).Append(" objects of a known type");
                    for (int i = 1; i < stat.Length; i++) if (stat[i] > 0) s.Append(", ").Append(stat[i]).Append(" wrong ").Append(WrsLayout.WhyText[i]);
                    s.Append(")");
                }
                Log(s.ToString());
                // Objects of the confirmed type exist, but in two searches in a row none passes the field checks:
                // the field data does not describe this game build correctly. Fall back to the basic checks
                // (plus pointer guard and second look) for the confirmed type only.
                int have;
                lock (setLock) have = targets.Count + pending.Count;
                if (!structural && L.Strong && typesConfirmed && have == 0 && stat[WrsLayout.WHY_OK] == 0 &&
                    stat[WrsLayout.WHY_ENUM] + stat[WrsLayout.WHY_FLAG] + stat[WrsLayout.WHY_FLOAT] > 0)
                {
                    if (++relaxVotes >= 2) relaxRequested = true;
                }
                else relaxVotes = 0;
            }
            if (structural && n > 0)
            {
                Dictionary<ulong, bool> seen = new Dictionary<ulong, bool>();
                lock (setLock) foreach (Cand c in pending.Values) seen[c.Vt] = true;
                bool conf;
                Dictionary<ulong, bool> ok = AcceptTypes(p, L, new List<ulong>(seen.Keys), out conf);
                if (conf) typesConfirmed = true;
                lock (setLock)
                {
                    foreach (ulong v in ok.Keys) vtables[v] = true;
                    List<ulong> drop = new List<ulong>();
                    foreach (KeyValuePair<ulong, Cand> kv in pending) if (!ok.ContainsKey(kv.Value.Vt)) drop.Add(kv.Key);
                    foreach (ulong o in drop) pending.Remove(o);
                }
            }
        }
        catch (Exception ex) { Log("Search: " + ex.Message); }
    }

    // ---------------- identifying the object type through the engine's type data ----------------
    static ulong PQ(Proc p, ulong a) { byte[] b = p.Read(a, 8); return b == null ? 0 : BitConverter.ToUInt64(b, 0); }
    static string PStr(Proc p, ulong a)
    {
        if (!p.InImage(a)) return null;
        byte[] b = p.Read(a, 96);
        if (b == null) return null;
        for (int i = 0; i < b.Length; i++)
        {
            if (b[i] == 0) return i > 0 ? Encoding.ASCII.GetString(b, 0, i) : null;
            if (b[i] < 0x20 || b[i] > 0x7E) return null;
        }
        return null;
    }
    // Names of a type and its base types (type info +0 -> member info; member info +0 name, +0x38 base type).
    static List<string> TypeChain(Proc p, ulong t)
    {
        List<string> r = new List<string>();
        for (int d = 0; d < 12 && p.InImage(t); d++)
        {
            ulong info = PQ(p, t);
            if (!p.InImage(info)) break;
            string n = PStr(p, PQ(p, info));
            if (n == null) break;
            r.Add(n);
            ulong sup = PQ(p, info + 0x38);
            if (sup == t) break;
            t = sup;
        }
        return r;
    }
    // 1 = the value at the start of the object belongs to WorldRenderSettings(Base) or a class derived from it,
    // -1 = it names a different type, 0 = not identifiable. The value is tried as type info itself and as a
    // vtable whose getType() function is a plain 'lea/mov rax,[rip+x]; ret'.
    int TypeOf(Proc p, ulong v, out string what)
    {
        List<ulong> types = new List<ulong>();
        types.Add(v);
        for (int i = 0; i < 12; i++)
        {
            ulong fn = PQ(p, v + (ulong)(i * 8));
            if (!p.InImage(fn)) break;
            for (int hop = 0; hop < 3; hop++)
            {
                byte[] c = p.Read(fn, 16);
                if (c == null) break;
                if (c[0] == 0xE9) { fn = (ulong)((long)fn + 5 + BitConverter.ToInt32(c, 1)); continue; }
                if (c[0] == 0x48 && (c[1] == 0x8D || c[1] == 0x8B) && c[2] == 0x05 && c[7] == 0xC3)
                {
                    ulong t = (ulong)((long)fn + 7 + BitConverter.ToInt32(c, 3));
                    if (c[1] == 0x8B) t = PQ(p, t);
                    if (!types.Contains(t)) types.Add(t);
                }
                break;
            }
        }
        string other = null;
        foreach (ulong t in types)
        {
            List<string> chain = TypeChain(p, t);
            if (chain.Count == 0) continue;
            bool wrs = false;
            foreach (string n in chain) if (n.StartsWith(WRS_NAME, StringComparison.Ordinal)) wrs = true;   // WorldRenderSettings(Base)
            if (wrs) { what = string.Join(" < ", chain.ToArray()); return 1; }
            if (other == null) other = string.Join(" < ", chain.ToArray());
        }
        what = other ?? "-";
        return other != null ? -1 : 0;
    }
    // Which object types may be written to. If at least one type is confirmed as WorldRenderSettings, only
    // confirmed types are used. Without engine field data an unconfirmed type is never used.
    Dictionary<ulong, bool> AcceptTypes(Proc p, WrsLayout L, List<ulong> vts, out bool confirmed)
    {
        Dictionary<ulong, bool> yes = new Dictionary<ulong, bool>(), rest = new Dictionary<ulong, bool>();
        confirmed = false;
        foreach (ulong v in vts)
        {
            string what;
            int r = TypeOf(p, v, out what);
            Log("Object type 0x" + (v - p.Base).ToString("X") + ": " + (r > 0 ? "confirmed" : r < 0 ? "other type" : "not identifiable") + " (" + what + ")");
            if (r > 0) yes[v] = true; else rest[v] = true;
        }
        if (yes.Count > 0) { confirmed = true; return yes; }
        if (!L.Strong)
        {
            Log("Safety: no object type could be confirmed and no field data is available - nothing will be written");
            return yes;
        }
        Log("No object type could be confirmed - relying on the field checks");
        return rest;
    }

    // Second look at new candidates: still the same type, still valid and practically unchanged.
    // Memory that is being freed or reused changes in the meantime; real settings objects do not.
    void Promote(Proc p, WrsLayout L)
    {
        long now = clock.ElapsedMilliseconds;
        List<KeyValuePair<ulong, Cand>> due = new List<KeyValuePair<ulong, Cand>>();
        lock (setLock) foreach (KeyValuePair<ulong, Cand> kv in pending) if (now - kv.Value.Seen >= SETTLE_MS) due.Add(kv);
        if (due.Count == 0) return;
        int added = 0, rejected = 0, capped = 0, fullOk = 0;
        foreach (KeyValuePair<ulong, Cand> kv in due)
        {
            ulong o = kv.Key;
            Cand c = kv.Value;
            byte[] b = p.Read(o, L.Size);
            bool ok = b != null && U64(b, 0) == c.Vt && L.Check(b, 0, p);
            if (ok)
            {
                int same = 0, tot = 0;
                for (int i = 8; i + 4 <= L.Size; i += 4)
                {
                    if (i == L.OffUp || i == L.OffFs || i == L.OffFg) continue;
                    tot++;
                    if (U32(b, i) == U32(c.Snap, i)) same++;
                }
                ok = same * 100 >= tot * 95;
            }
            if (ok && !L.Strong && PointerLike(p, b, L)) ok = false;
            lock (setLock)
            {
                pending.Remove(o);
                if (!ok)
                {
                    int k;
                    strikes.TryGetValue(o, out k);
                    strikes[o] = k + 1;
                    rejected++;
                    continue;
                }
                if (targets.Count >= MAX_TARGETS) { capped++; continue; }
                targets[o] = c.Vt;
                added++;
            }
            if (L.Full != null)
            {
                byte[] f = p.Read(o, L.Full.Size);
                if (f != null && L.Full.Check(f, 0, p)) fullOk++;
            }
        }
        int total;
        lock (setLock) total = targets.Count;
        if (added > 0 || rejected > 0 || capped > 0)
            Log("Settings objects: " + added + " accepted" +
                (L.Full != null && added > 0 ? " (" + fullOk + " of them also match the full 0x" + L.Full.Size.ToString("X") + " layout)" : "") +
                ", " + rejected + " rejected at the second look" + (capped > 0 ? ", " + capped + " over the limit of " + MAX_TARGETS : "") +
                " - " + total + " in use");
    }
    // Only used without engine field data: refuse an object if a field we would write to is part of
    // something that looks like a pointer to committed memory.
    static bool PointerLike(Proc p, byte[] b, WrsLayout L)
    {
        int[] offs = { L.OffUp, L.OffFs, L.OffFg };
        foreach (int off in offs)
        {
            int a = off & ~7;
            if (a + 8 > b.Length) continue;
            ulong q = BitConverter.ToUInt64(b, a);
            if (q < 0x10000 || q >= 0x800000000000UL) continue;
            Proc.MBI m;
            if (p.Query(q, out m) && m.State == 0x1000) return true;
        }
        return false;
    }

    void Enforce(Proc p, WrsLayout L)
    {
        List<KeyValuePair<ulong, ulong>> list = new List<KeyValuePair<ulong, ulong>>();
        lock (setLock) foreach (KeyValuePair<ulong, ulong> kv in targets) list.Add(kv);
        int md = mode;
        bool useFsr = fam == FAM_FSR || !nvidia;
        if (useFsr && !L.HasFsr) md = 0;    // FSR 3 not found in this game version: leave the game's own TAA
        uint upWant = md == 0 ? 0 : (useFsr ? L.UpFsr[md] : L.Up[md]);
        uint fsWant = md == 0 ? L.FsNone : L.FsTu;
        int dropped = 0;
        foreach (KeyValuePair<ulong, ulong> kv in list)
        {
            ulong o = kv.Key;
            // check the object again right before every write: if it was freed or replaced, leave it alone
            byte[] b = p.Read(o, L.Size);
            if (b == null || U64(b, 0) != kv.Value || !L.Check(b, 0, p)) { lock (setLock) targets.Remove(o); dropped++; continue; }
            if (U32(b, L.OffFs) != fsWant) p.Write32(o + (ulong)L.OffFs, fsWant);
            if (md != 0 && U32(b, L.OffUp) != upWant) p.Write32(o + (ulong)L.OffUp, upWant);
            if (U32(b, L.OffFg) != L.FgOff) p.Write32(o + (ulong)L.OffFg, L.FgOff);
        }
        if (dropped > 0)
        {
            int n;
            lock (setLock) n = targets.Count;
            Log("Settings objects: " + dropped + " no longer valid and dropped - " + n + " in use");
        }
    }

    void Worker()
    {
        Dictionary<int, bool> done = new Dictionary<int, bool>();
        while (!closing)
        {
            int pid = -1;
            try
            {
                foreach (Process x in Process.GetProcessesByName("FC27"))
                {
                    if (pid < 0 && !done.ContainsKey(x.Id)) pid = x.Id;
                    x.Dispose();
                }
            }
            catch { }
            if (pid < 0) { state = "waiting for FC 27"; Thread.Sleep(500); continue; }
            done[pid] = true;
            try { Session(pid); }
            catch (Exception ex) { Log("ERROR: " + ex); }
            lock (setLock) { targets.Clear(); regions.Clear(); vtables.Clear(); }
            state = "waiting for FC 27";
        }
    }

    void Session(int pid)
    {
        Proc p = Proc.Open(pid);
        if (p.H == IntPtr.Zero)
        {
            Log("No access to FC27.exe (PID " + pid + ", error " + p.LastError + ")");
            Thread.Sleep(3000);
            bool alive = false;
            try { using (Process x = Process.GetProcessById(pid)) alive = !x.HasExited; } catch (ArgumentException) { alive = false; } catch { alive = true; }
            if (alive)
            {
                state = "no access to FC 27";
                Osd("No access to FC 27.\nThe game must be started through the FC Live Editor\nand the switch needs administrator rights.", 9000, true);
            }
            return;
        }
        try { Run(p); }
        finally { p.Close(); }
    }

    void Run(Proc p)
    {
        Stopwatch life = Stopwatch.StartNew();
        ulong imgBase = 0x140000000UL;
        byte[] hdr = null;
        while (!closing && p.Alive() && life.ElapsedMilliseconds < 30000)
        {
            try { using (Process x = Process.GetProcessById(p.Pid)) imgBase = (ulong)(long)x.MainModule.BaseAddress; } catch { }
            hdr = p.Read(imgBase, 0x1000);
            if (hdr != null && hdr[0] == 'M' && hdr[1] == 'Z') break;
            hdr = null;
            Thread.Sleep(50);
        }
        if (hdr == null) return;
        int pe = BitConverter.ToInt32(hdr, 0x3C);
        if (pe <= 0 || pe > 0xE00 || U32(hdr, pe) != 0x4550) { Log("PE header unreadable"); return; }
        uint stamp = U32(hdr, pe + 8);
        p.Base = imgBase;
        p.ImgSize = U32(hdr, pe + 24 + 56);

        // The launcher starts FC27.exe once briefly and then restarts it - skip that short-lived instance.
        while (!closing && p.Alive() && life.ElapsedMilliseconds < 2500) Thread.Sleep(100);
        if (!p.Alive()) { Log("FC27.exe PID " + p.Pid + " was only the short launcher process"); return; }
        Log("FC27.exe PID " + p.Pid + " detected, version 0x" + stamp.ToString("X8") + ", base 0x" + imgBase.ToString("X"));

        state = "reading game data ...";
        WrsLayout L = null;
        int attempts = stamp == BUILTIN_STAMP ? 6 : 24;
        for (int attempt = 0; attempt < attempts && L == null && !closing && p.Alive(); attempt++)
        {
            try { L = new Resolver(p, Log).Resolve(); }
            catch (Exception ex) { Log("Engine analysis: " + ex.Message); }
            if (L == null)
            {
                state = "reading game data ... (attempt " + (attempt + 2) + ")";
                for (int t = 0; t < 50 && !closing && p.Alive(); t++) Thread.Sleep(100);
            }
        }
        if (L == null && stamp == BUILTIN_STAMP && p.Alive() && !closing)
        {
            Log("Engine analysis failed - using the built-in values with extra safety checks");
            L = WrsLayout.Builtin(imgBase);
        }
        if (L == null)
        {
            if (!p.Alive() || closing) return;
            state = "this FC 27 version is not supported";
            Log("Graphics settings not found in this game version - switch inactive.");
            Osd("DLSS Switch: The graphics settings were not found\nin this FC 27 version. Switch inactive.", 9000, true);
            while (!closing && p.Alive()) Thread.Sleep(1000);
            return;
        }
        if (stamp == BUILTIN_STAMP && L.Strong)
        {
            WrsLayout B = WrsLayout.Builtin(imgBase);
            bool same = B.OffUp == L.OffUp && B.OffFs == L.OffFs && B.OffFg == L.OffFg && B.OffN == L.OffN && B.OffCs == L.OffCs;
            for (int i = 1; i <= 5; i++) if (B.Up[i] != L.Up[i] || B.UpFsr[i] != L.UpFsr[i]) same = false;
            if (B.HasFsr != L.HasFsr) same = false;
            if (B.FsNone != L.FsNone || B.FsTu != L.FsTu || B.FgOff != L.FgOff) same = false;
            if (!same)
            {
                Log("MISMATCH in engine analysis: Up=0x" + L.OffUp.ToString("X") + " FS=0x" + L.OffFs.ToString("X") + " FG=0x" + L.OffFg.ToString("X") +
                    " N=0x" + L.OffN.ToString("X") + " CS=0x" + L.OffCs.ToString("X") + " - using the built-in values with extra safety checks");
                L = B;
            }
            else if (L.Size > B.Size)
            {
                // The engine describes the whole object; the switch reads and checks the first 0x2E8 bytes,
                // which is the part that is known to work. The full layout is still tested for the log.
                Log("Engine analysis matches the known values (full object 0x" + L.Size.ToString("X") + " bytes: " + L.Counts() + ")");
                L = L.Window(B.Size);
            }
            else Log("Engine analysis matches the known values.");
        }
        Log("Layout: " + L.Source + "  Up=0x" + L.OffUp.ToString("X") + " FS=0x" + L.OffFs.ToString("X") + " FG=0x" + L.OffFg.ToString("X") +
            " N=0x" + L.OffN.ToString("X") + " CS=0x" + L.OffCs.ToString("X") + " size=0x" + L.Size.ToString("X") +
            "  DLSS values " + L.Up[1].ToString("X") + "/" + L.Up[2].ToString("X") + "/" + L.Up[3].ToString("X") + "/" + L.Up[4].ToString("X") + "/" + L.Up[5].ToString("X") +
            "  FSR3 values " + (L.HasFsr ? L.UpFsr[1].ToString("X") + "/" + L.UpFsr[2].ToString("X") + "/" + L.UpFsr[3].ToString("X") + "/" + L.UpFsr[4].ToString("X") + "/" + L.UpFsr[5].ToString("X") : "missing"));
        Log("Checks: " + (L.Strong ? "full field check (" + L.Counts() + ")" : "basic check only (no field data)"));

        List<ulong> vtRva = new List<ulong>();
        if (cfg.NoDefaults) Log("Test mode: without saved and built-in object types");
        else
        {
            LoadCache(stamp, vtRva);
            if (stamp == BUILTIN_STAMP)
                foreach (ulong v in BuiltinVtRva) if (!vtRva.Contains(v)) vtRva.Add(v);
        }
        Dictionary<ulong, bool> accepted = new Dictionary<ulong, bool>();
        List<ulong> vts = new List<ulong>();
        bool confirmed = false;
        if (vtRva.Count > 0)
        {
            foreach (ulong r in vtRva) vts.Add(imgBase + r);
            accepted = AcceptTypes(p, L, vts, out confirmed);
        }
        typesConfirmed = confirmed;
        relaxRequested = false;
        relaxVotes = 0;
        lock (setLock)
        {
            targets.Clear(); pending.Clear(); strikes.Clear(); regions.Clear(); vtables.Clear();
            foreach (ulong v in accepted.Keys) vtables[v] = true;
        }
        if (vtRva.Count > 0 && accepted.Count == 0)
        {
            state = "inactive (settings could not be identified safely)";
            Osd("DLSS Switch: the graphics settings could not be\nidentified safely in this FC 27 version. Switch inactive.", 9000, true);
            while (!closing && p.Alive()) Thread.Sleep(1000);
            Log("FC27.exe PID " + p.Pid + " exited after " + (life.ElapsedMilliseconds / 1000) + " s");
            return;
        }
        Log("Object types in use: " + accepted.Count + " of " + vtRva.Count);

        state = "searching graphics settings ...";
        Thread scan = null;
        bool scanIsFull = false;
        long nextFull = life.ElapsedMilliseconds + 1000;
        long lastRegion = life.ElapsedMilliseconds;
        int okScans = 0;
        bool announced = false, cached = false;
        // early in the game start the code may not be readable yet: try to confirm the types again later
        long recheckAt = confirmed || vts.Count == 0 ? long.MaxValue : life.ElapsedMilliseconds + 20000;
        int rechecks = 0;
        while (!closing && p.Alive())
        {
            if (life.ElapsedMilliseconds >= recheckAt)
            {
                recheckAt = ++rechecks < 3 ? life.ElapsedMilliseconds + 40000 : long.MaxValue;
                bool conf;
                Dictionary<ulong, bool> again = AcceptTypes(p, L, vts, out conf);
                if (conf)
                {
                    recheckAt = long.MaxValue;
                    lock (setLock)
                    {
                        vtables.Clear();
                        foreach (ulong v in again.Keys) vtables[v] = true;
                        List<ulong> drop = new List<ulong>();
                        foreach (KeyValuePair<ulong, ulong> kv in targets) if (!again.ContainsKey(kv.Value)) drop.Add(kv.Key);
                        foreach (ulong o in drop) targets.Remove(o);
                        drop.Clear();
                        foreach (KeyValuePair<ulong, Cand> kv in pending) if (!again.ContainsKey(kv.Value.Vt)) drop.Add(kv.Key);
                        foreach (ulong o in drop) pending.Remove(o);
                    }
                    Log("Object types confirmed now - using " + again.Count + " of " + vts.Count);
                    typesConfirmed = true;
                }
            }
            if (relaxRequested && (scan == null || !scan.IsAlive))
            {
                relaxRequested = false;
                L = L.Basic();
                Log("The field data does not fit the objects of the confirmed type - using basic checks for that type only");
                nextFull = 0;
            }

            bool scanning = scan != null && scan.IsAlive;
            if (!scanning)
            {
                if (scan != null && scanIsFull)
                {
                    // a full search just finished: plan the next one
                    int cnt;
                    lock (setLock) cnt = targets.Count + pending.Count;
                    if (cnt > 0) okScans++;
                    if (cnt == 0) nextFull = life.ElapsedMilliseconds + 10000;
                    else if (okScans == 1) nextFull = life.ElapsedMilliseconds + 45000;
                    else if (okScans == 2) nextFull = life.ElapsedMilliseconds + 120000;
                    else nextFull = long.MaxValue;
                    scanIsFull = false;
                }
                if (rescanRequested) { rescanRequested = false; nextFull = 0; }
                if (life.ElapsedMilliseconds >= nextFull)
                {
                    scanIsFull = true;
                    nextFull = long.MaxValue;
                    scan = new Thread(delegate () { ScanJob(p, L, true); });
                    scan.IsBackground = true;
                    scan.Priority = ThreadPriority.BelowNormal;
                    scan.Start();
                }
                else if (life.ElapsedMilliseconds - lastRegion > 15000)
                {
                    lastRegion = life.ElapsedMilliseconds;
                    int rc;
                    lock (setLock) rc = regions.Count;
                    if (rc > 0)
                    {
                        scan = new Thread(delegate () { ScanJob(p, L, false); });
                        scan.IsBackground = true;
                        scan.Priority = ThreadPriority.BelowNormal;
                        scan.Start();
                    }
                }
            }

            Promote(p, L);
            Enforce(p, L);
            int n;
            lock (setLock) n = targets.Count;
            if (n > 0 && !cached) { cached = true; SaveCache(stamp, imgBase); }
            bool busy = scan != null && scan.IsAlive && scanIsFull;
            if (n > 0) state = busy ? "active (searching for more settings)" : "active";
            else state = busy ? "searching graphics settings ..." : "waiting for the graphics settings ...";
            if (n > 0 && !announced && life.ElapsedMilliseconds > 6000) { announced = true; ShowStatus(true); }
            Thread.Sleep(200);
        }
        if (scan != null) scan.Join(3000);
        Log("FC27.exe PID " + p.Pid + " exited after " + (life.ElapsedMilliseconds / 1000) + " s");

    }
}
