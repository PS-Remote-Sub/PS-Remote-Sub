using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PSRemoteSub.Core.Tv;

/// <summary>What <c>dumpsys package th.psrs.tvoverlay</c> says about the TV app. <c>null</c> field = not found.</summary>
public sealed record TvPackageInfo(int? VersionCode, string? VersionName, string? FirstInstall, string? LastUpdate, bool? Enabled);

/// <summary>Our accessibility service on the TV. <c>null</c> = could not be determined. Other services are only counted, never named.</summary>
public sealed record TvA11yInfo(bool? Listed, bool? Bound, int OtherEnabledCount);

/// <summary>The TV app's <c>psrs-diag-state v1</c> block.</summary>
public sealed record TvAppState(bool Running, IReadOnlyDictionary<string, string> Values, IReadOnlyList<string> Heard);

/// <summary>
/// Everything <see cref="TvDiagText.Summary"/> needs; gathered by the collector. <paramref name="LastCrashLine"/> is the
/// <c>time=... thread=...</c> line of the crash record, <c>null</c> when there is none. <paramref name="CrashChannelRead"/>
/// is <c>true</c> when the crash channel answered (so a <c>null</c> line then means "no crash"), <c>false</c> when it was unavailable.
/// </summary>
public sealed record TvDiagFacts(
    DateTimeOffset Time,
    string PcVersion,
    string Os,
    bool AdbAuthorized,
    string? AdbFailure,
    IReadOnlyDictionary<string, string>? Props,
    TvPackageInfo? Package,
    int? NewestApkVersion,
    TvA11yInfo? A11y,
    TvAppState? State,
    string? PcIdHash,
    IReadOnlyList<string> TvClients,
    string? LastCrashLine,
    bool CrashChannelRead = false);

/// <summary>
/// Pure text logic for the TV diagnostics dump: privacy filters, parsers for the TV's provider text and the
/// <c>summary.txt</c> checks. Nothing here throws on any input or touches the device or disk.
/// </summary>
public static class TvDiagText
{
    private const string Package = "th.psrs.tvoverlay";
    private const string StateHeader = "psrs-diag-state v1";

    private static readonly HashSet<string> PropWhitelist = new(StringComparer.Ordinal)
    {
        "ro.product.manufacturer", "ro.product.brand", "ro.product.model", "ro.product.device",
        "ro.build.version.release", "ro.build.version.sdk", "ro.build.version.security_patch", "ro.build.fingerprint",
        "ro.config.low_ram", "ro.board.platform", "ro.hardware", "ro.product.cpu.abi",
        "persist.sys.locale", "ro.product.locale",
    };

    private static readonly Regex PropLine = new(@"^\[(?<k>[^\]]+)\]:\s*\[(?<v>.*)\]\s*$", RegexOptions.Compiled);
    private static readonly Regex ThreadTime = new(
        @"^\d\d-\d\d\s+\d\d:\d\d:\d\d\.\d+\s+(?<pid>\d+)\s+\d+\s+[VDIWEFAS]\s+(?<tag>.*?)\s*:\s", RegexOptions.Compiled);
    // Package-looking dotted tokens; a mention-only logcat line is kept only if every one is ours or a framework name.
    private static readonly Regex PackageToken = new(@"\b[a-z][a-z0-9_]*(?:\.[a-z0-9_]+){2,}\b", RegexOptions.Compiled);
    private static readonly string[] FrameworkPrefixes = ["android.", "java.", "javax.", "kotlin.", "dalvik.", "libcore."];

    /// <summary>True when <paramref name="line"/> has a package-like token that is neither ours nor a framework one.</summary>
    private static bool NamesOtherPackage(string line) =>
        PackageToken.Matches(line.Replace(Package, "", StringComparison.Ordinal))
            .Any(m => !FrameworkPrefixes.Any(f => m.Value.StartsWith(f, StringComparison.Ordinal)));

    /// <summary>First 8 lowercase hex chars of SHA-256(UTF-8 pcId); same function as on the TV.</summary>
    public static string PcIdHash(string pcId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(pcId ?? ""));
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    /// <summary>The <c>[key]: [value]</c> lines of <c>getprop</c> whose key is on the whitelist; everything else is dropped.</summary>
    public static IReadOnlyDictionary<string, string> WhitelistedProps(string getpropOutput)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in Lines(getpropOutput))
        {
            var m = PropLine.Match(raw.Trim());
            if (m.Success && PropWhitelist.Contains(m.Groups["k"].Value))
                result[m.Groups["k"].Value] = m.Groups["v"].Value;
        }
        return result;
    }

    /// <summary>
    /// Keeps threadtime lines from one of <paramref name="pids"/>, with tag <c>PsrsTv</c>, or mentioning the TV package
    /// (unless the line also names another app); continuation lines (stack traces) only right after a kept line.
    /// </summary>
    public static string FilterLogcat(string logcat, IReadOnlyCollection<int> pids)
    {
        var kept = new List<string>();
        var previousKept = false;
        foreach (var line in Lines(logcat))
        {
            var m = ThreadTime.Match(line);
            if (!m.Success)
            {
                // "--------- beginning of main" and blanks are not continuations; indented/other text after a kept line is.
                var isSeparator = line.StartsWith("---------", StringComparison.Ordinal);
                previousKept = previousKept && !isSeparator && line.Length > 0;
                if (previousKept)
                    kept.Add(line);
                continue;
            }
            var pid = int.TryParse(m.Groups["pid"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : -1;
            var ours = (pids is not null && pids.Contains(pid)) || m.Groups["tag"].Value == "PsrsTv";
            if (!ours && line.Contains(Package, StringComparison.Ordinal))
            {
                ours = !line.Contains("PackageSetting{", StringComparison.Ordinal) && !NamesOtherPackage(line);
            }
            previousKept = ours;
            if (ours)
                kept.Add(line);
        }
        return string.Join('\n', kept);
    }

    /// <summary>Version, install times and enabled state of the TV app, or <c>null</c> when the package is not in the output.</summary>
    public static TvPackageInfo? ParsePackage(string dumpsys)
    {
        var lines = Lines(dumpsys).ToList();
        var start = lines.FindIndex(l => l.TrimStart().StartsWith($"Package [{Package}]", StringComparison.Ordinal));
        if (start < 0)
            return null;
        int? code = null;
        string? name = null, first = null, last = null;
        bool? enabled = null;
        for (var i = start + 1; i < lines.Count; i++)
        {
            var t = lines[i].Trim();
            if (i > start + 1 && lines[i].Length > 0 && !char.IsWhiteSpace(lines[i][0]))
                break; // next top-level section
            if (code is null && t.StartsWith("versionCode=", StringComparison.Ordinal))
            {
                var digits = new string(t["versionCode=".Length..].TakeWhile(char.IsAsciiDigit).ToArray());
                if (int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var c))
                    code = c;
            }
            else if (name is null && t.StartsWith("versionName=", StringComparison.Ordinal))
                name = t["versionName=".Length..].Trim();
            else if (first is null && t.StartsWith("firstInstallTime=", StringComparison.Ordinal))
                first = t["firstInstallTime=".Length..].Trim();
            else if (last is null && t.StartsWith("lastUpdateTime=", StringComparison.Ordinal))
                last = t["lastUpdateTime=".Length..].Trim();
            else if (enabled is null && t.StartsWith("User 0:", StringComparison.Ordinal))
            {
                var m = Regex.Match(t, @"\benabled=(\d+)");
                if (m.Success)
                    enabled = m.Groups[1].Value is "0" or "1"; // 0 default, 1 enabled; 2..4 disabled variants
            }
        }
        return code is null && name is null ? null : new TvPackageInfo(code, name, first, last, enabled);
    }

    /// <summary>
    /// Listed = ours is in <c>enabled_accessibility_services</c> (<c>null</c> when the setting is unknown);
    /// Bound = ours is among the "Bound services" of <c>dumpsys accessibility</c> (<c>null</c> when it has no such line;
    /// <c>false</c> when the master switch <paramref name="accessibilityEnabled"/> is <c>0</c>, nothing can be bound then).
    /// Other services are counted, never named.
    /// </summary>
    public static TvA11yInfo ParseAccessibility(string? enabledSetting, string? accessibilityEnabled, string? dumpsys)
    {
        bool? listed = null;
        var others = 0;
        if (!string.IsNullOrWhiteSpace(enabledSetting))
        {
            var entries = enabledSetting.Trim().Equals("null", StringComparison.OrdinalIgnoreCase)
                ? []
                : enabledSetting.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            listed = false;
            foreach (var e in entries)
            {
                if (e.Equals($"{Package}/.OverlayA11yService", StringComparison.OrdinalIgnoreCase)
                    || e.Equals($"{Package}/{Package}.OverlayA11yService", StringComparison.OrdinalIgnoreCase))
                    listed = true;
                else
                    others++;
            }
        }

        bool? bound = null;
        foreach (var line in Lines(dumpsys))
        {
            var idx = line.IndexOf("Bound services:", StringComparison.Ordinal);
            if (idx < 0)
                continue;
            bound = line.Contains("label=PSRS TV Overlay", StringComparison.Ordinal);
            break;
        }
        if (bound is null && accessibilityEnabled?.Trim() == "0")
            bound = false;
        return new TvA11yInfo(listed, bound, others);
    }

    /// <summary>Parses a <c>psrs-diag-state v1</c> block (ends at a <c>---</c> line); <c>null</c> unless the text starts with the header.</summary>
    public static TvAppState? ParseState(string? text)
    {
        if (text is null)
            return null;
        var lines = Lines(text).ToList();
        var first = lines.FindIndex(l => l.Trim().Length > 0);
        if (first < 0 || lines[first].Trim() != StateHeader)
            return null;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var heard = new List<string>();
        for (var i = first + 1; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (line == "---")
                break;
            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = line[..eq];
            var value = line[(eq + 1)..];
            if (key == "pc.heard")
                heard.Add(value);
            else
                values[key] = value;
        }
        return new TvAppState(values.TryGetValue("service.running", out var r) && r == "true", values, heard);
    }

    /// <summary>
    /// Splits the output of <c>dumpsys activity service ...OverlayA11yService</c>, whose content is indented, into the
    /// state block (header up to <c>---</c>) and the log block after it. Both <c>null</c> when the state header is absent.
    /// </summary>
    public static (string? State, string? Log) SplitDumpsysService(string? text)
    {
        var lines = Lines(text).ToList();
        var h = lines.FindIndex(l => l.Trim() == StateHeader);
        if (h < 0)
            return (null, null);
        var indent = lines[h].Length - lines[h].TrimStart().Length;
        string Dedent(string l)
        {
            var n = 0;
            while (n < indent && n < l.Length && l[n] == ' ')
                n++;
            return l[n..];
        }
        var state = new List<string>();
        var i = h;
        for (; i < lines.Count && lines[i].Trim() != "---"; i++)
            state.Add(Dedent(lines[i]));
        var log = new List<string>();
        for (var j = i + 1; j < lines.Count; j++)
            log.Add(Dedent(lines[j]));
        var logText = string.Join('\n', log).TrimEnd();
        return (string.Join('\n', state), i < lines.Count && logText.Length > 0 ? logText : null);
    }

    /// <summary>Whether the first non-blank line of <paramref name="text"/> is exactly <paramref name="header"/>.</summary>
    public static bool HasHeader(string? text, string header)
    {
        foreach (var line in Lines(text))
        {
            if (line.Trim().Length == 0)
                continue;
            return line.Trim() == header;
        }
        return false;
    }

    /// <summary>The <c>summary.txt</c> content: header lines, then checks 1 to 8 in order.</summary>
    public static string Summary(TvDiagFacts facts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("PS Remote Sub - TV diagnostics / ข้อมูลแก้ปัญหาทีวี");
        sb.AppendLine($"Time: {facts.Time.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)}");
        sb.AppendLine($"PC app: {facts.PcVersion}");
        sb.AppendLine($"PC OS: {facts.Os}");
        sb.AppendLine($"PC id hash: {facts.PcIdHash ?? "?"}");
        sb.AppendLine();

        void Check(int n, char mark, string thai, string english) => sb.AppendLine($"{n}. {mark} {thai} / {english}");
        const char Ok = '✔', Bad = '✘', Unknown = '?';
        var reached = facts.AdbAuthorized;
        var props = facts.Props;
        string Prop(string k) => props is not null && props.TryGetValue(k, out var v) && v.Length > 0 ? v : "?";

        // 1
        if (reached)
            Check(1, Ok, "คอมเชื่อมต่อทีวีผ่าน adb ได้และได้รับอนุญาตแล้ว", "PC reached the TV over adb and is authorized");
        else
            Check(1, Bad, "คอมเชื่อมต่อทีวีไม่ได้หรือทีวียังไม่อนุญาต" + Why(facts.AdbFailure, " - "),
                "PC could not reach the TV over adb or it is not authorized" + Why(facts.AdbFailure, " - "));

        // 2
        if (!reached || !int.TryParse(Prop("ro.build.version.sdk"), NumberStyles.None, CultureInfo.InvariantCulture, out var sdk))
            Check(2, Unknown, "ไม่ทราบเวอร์ชัน Android (ต้อง 8.0 ขึ้นไป)", "Android version unknown (needs 8.0+)");
        else if (sdk >= 26)
            Check(2, Ok, $"Android {Prop("ro.build.version.release")} (SDK {sdk}) ใช้ได้", $"Android {Prop("ro.build.version.release")} (SDK {sdk}) is supported");
        else
            Check(2, Bad, $"Android {Prop("ro.build.version.release")} (SDK {sdk}) เก่าเกินไป ต้อง 8.0 ขึ้นไป", $"Android {Prop("ro.build.version.release")} (SDK {sdk}) is too old, needs 8.0+ (SDK 26)");

        // 3
        if (!reached)
            Check(3, Unknown, "ไม่ทราบว่าติดตั้งแอปทีวีแล้วหรือยัง", "TV app install state unknown");
        else if (facts.Package?.VersionCode is not int installed)
            Check(3, Bad, "ยังไม่ได้ติดตั้งแอปบนทีวี", "TV app is not installed");
        else if (facts.NewestApkVersion is int newest && installed < newest)
            Check(3, Bad, $"แอปบนทีวีเก่า (เวอร์ชัน {installed}, ใหม่สุด {newest}) ควรอัปเดต", $"TV app is outdated (version {installed}, newest {newest}), update it");
        else
            Check(3, Ok, $"ติดตั้งแอปบนทีวีแล้ว เวอร์ชัน {facts.Package!.VersionName ?? "?"} ({installed})", $"TV app installed, version {facts.Package!.VersionName ?? "?"} ({installed})");

        // 4
        var a11y = facts.A11y;
        if (!reached || a11y is null)
            Check(4, Unknown, "ไม่ทราบสถานะบริการการช่วยเหลือพิเศษ", "Accessibility service state unknown");
        else if (a11y.Listed == false)
            Check(4, Bad, "ยังไม่ได้เปิดบริการการช่วยเหลือพิเศษของแอปบนทีวี", "Accessibility service is not enabled on the TV");
        else if (a11y.Bound == true)
            Check(4, Ok, "เปิดบริการการช่วยเหลือพิเศษและทำงานอยู่", "Accessibility service is enabled and running");
        else if (a11y.Bound == false)
            Check(4, Bad, "เปิดบริการไว้แต่ระบบยังไม่ได้เริ่มให้ทำงาน", "Accessibility service is enabled but not bound/running");
        else
            Check(4, Unknown, "ไม่ทราบว่าบริการทำงานอยู่หรือไม่", "Accessibility service enabled state known but running state unknown");

        // 5 6 7 need the TV app's own state
        var state = facts.State;
        string V(string k) => state is not null && state.Values.TryGetValue(k, out var v) ? v : "";
        if (!reached || state is null)
            Check(5, Unknown, "ไม่ได้ข้อมูลจากแอปทีวี (แอปรุ่นเก่าหรือยังไม่ทำงาน)", "No state from the TV app (old version or not running)");
        else if (!state.Running)
            Check(5, Bad, "บริการของแอปบนทีวีไม่ได้ทำงาน", "TV app service is not running");
        else if (V("overlay.attached") == "false")
            Check(5, Bad, "ระบบไม่ยอมให้แอปวางหน้าต่างซับบนจอ" + Why(V("overlay.error"), ": "),
                "The system refused the overlay window" + Why(V("overlay.error"), ": "));
        else if (V("overlay.attached") == "true")
            Check(5, Ok, $"วางหน้าต่างซับบนจอแล้ว (drawing={V("drawing")})", $"Overlay window attached (drawing={V("drawing")})");
        else
            Check(5, Unknown, "ไม่ทราบสถานะหน้าต่างซับ", "Overlay window state unknown");

        // 6
        var connected = V("pc.connected");
        var tvSeesUs = facts.PcIdHash is { Length: > 0 } hash && connected.StartsWith(hash + " ", StringComparison.Ordinal);
        var heardUs = facts.PcIdHash is { Length: > 0 } h2 && state is not null && state.Heard.Any(x => x.StartsWith(h2 + " ", StringComparison.Ordinal));
        if (!reached)
            Check(6, Unknown, "ไม่ทราบว่าทีวีต่อกับคอมนี้อยู่หรือไม่", "Unknown whether the TV is connected to this PC");
        else if (tvSeesUs)
            Check(6, Ok, "แอปทีวีต่อกับคอมเครื่องนี้อยู่", "TV app is connected to this PC");
        else if (facts.TvClients.Count > 0)
            Check(6, state is null ? Ok : Unknown,
                state is null ? "คอมเห็นทีวีต่ออยู่" : "คอมเห็นทีวีต่ออยู่ แต่แอปทีวีรายงานไม่ตรงกัน",
                state is null ? "PC sees a TV connected" : "PC sees a TV connected but the TV app reports otherwise");
        else if (state is null)
            Check(6, Unknown, "ไม่ทราบว่าทีวีต่อกับคอมนี้อยู่หรือไม่", "Unknown whether the TV is connected to this PC");
        else
        {
            var detail = $"sse.state={V("sse.state")}" + (V("sse.lastFailure").Length > 0 ? $", sse.lastFailure={V("sse.lastFailure")}" : "");
            Check(6, Bad, (heardUs ? "ทีวีได้ยินคอมนี้แต่ยังไม่ได้ต่อ" : "ทีวีไม่ได้ยินสัญญาณจากคอมนี้และไม่ได้ต่ออยู่") + $" ({detail})",
                (heardUs ? "TV hears this PC but is not connected" : "TV has not heard this PC and is not connected") + $" ({detail})");
        }

        // 7
        if (!string.IsNullOrWhiteSpace(facts.LastCrashLine))
            Check(7, Bad, $"แอปบนทีวีเคยค้าง ล่าสุด {facts.LastCrashLine.Trim()}", $"TV app crashed recently, last: {facts.LastCrashLine.Trim()}");
        else if (!reached || !facts.CrashChannelRead)
            Check(7, Unknown, "ไม่ทราบว่าแอปทีวีเคยค้างหรือไม่", "Unknown whether the TV app crashed");
        else
            Check(7, Ok, "แอปบนทีวีไม่เคยค้าง", "TV app has not crashed");

        // 8: informational, no mark
        sb.AppendLine($"8. ข้อมูลเครื่องทีวี / Device info: model {Prop("ro.product.model")}, Android {Prop("ro.build.version.release")}, " +
                      $"low_ram {Prop("ro.config.low_ram")}, build {Prop("ro.build.fingerprint")}");
        return sb.ToString();
    }

    private static string Why(string? reason, string prefix) =>
        string.IsNullOrWhiteSpace(reason) ? "" : prefix + reason.Trim();

    private static IEnumerable<string> Lines(string? text) =>
        string.IsNullOrEmpty(text) ? [] : text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
}
