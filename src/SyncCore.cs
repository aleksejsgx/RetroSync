using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RetroSync
{
    static class Util
    {
        public static readonly Encoding Utf8 = new UTF8Encoding(false);

        public static string Sha(byte[] b)
        {
            using (var s = SHA256.Create()) return BitConverter.ToString(s.ComputeHash(b)).Replace("-", "");
        }

        public static string Sha(string text) { return Sha(Utf8.GetBytes(text)); }

        public static string FmtUtc(DateTime? utc)
        {
            return utc.HasValue && utc.Value.Year > 1980 ? utc.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm") : "\u2014";
        }

        public static string Size(long b)
        {
            if (b < 1024) return b + " B";
            if (b < 1048576) return (b / 1024.0).ToString("0.#") + " KB";
            return (b / 1048576.0).ToString("0.0") + " MB";
        }

        public static string JoinRel(string a, string b)
        {
            a = (a ?? "").Replace('\\', '/').Trim('/');
            b = (b ?? "").Replace('\\', '/').Trim('/');
            if (a.Length == 0) return b;
            if (b.Length == 0) return a;
            return a + "/" + b;
        }

        public static string RemoteJoin(string root, string rel)
        {
            string r = "/" + (root ?? "").Replace('\\', '/').Trim('/');
            if (r == "/") r = "";
            return r + "/" + (rel ?? "").Replace('\\', '/').Trim('/');
        }

        public static string LocalJoin(string root, string rel)
        {
            return Path.Combine(root, (rel ?? "").Replace('/', '\\'));
        }

        public static string StripBom(string s) { return s.Length > 0 && s[0] == '\uFEFF' ? s.Substring(1) : s; }
    }

    // Glob matching: '**' any chars, '*' any chars except '/', '?' one char except '/'.
    // A pattern without '/' is matched against the file name only.
    static class Glob
    {
        static readonly Dictionary<string, Regex> cache = new Dictionary<string, Regex>();

        public static Regex ToRegex(string pattern)
        {
            Regex r;
            lock (cache)
            {
                if (cache.TryGetValue(pattern, out r)) return r;
                var sb = new StringBuilder("^");
                for (int i = 0; i < pattern.Length; i++)
                {
                    char c = pattern[i];
                    if (c == '*')
                    {
                        if (i + 1 < pattern.Length && pattern[i + 1] == '*') { sb.Append(".*"); i++; }
                        else sb.Append("[^/]*");
                    }
                    else if (c == '?') sb.Append("[^/]");
                    else sb.Append(Regex.Escape(c.ToString()));
                }
                sb.Append("$");
                r = new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                cache[pattern] = r;
                return r;
            }
        }

        public static bool MatchPath(string pattern, string relPath)
        {
            relPath = relPath.Replace('\\', '/');
            string target = pattern.Contains("/") ? relPath : relPath.Substring(relPath.LastIndexOf('/') + 1);
            return ToRegex(pattern).IsMatch(target);
        }

        public static bool AnyPath(IEnumerable<string> patterns, string relPath)
        {
            return patterns != null && patterns.Any(p => !string.IsNullOrEmpty(p) && MatchPath(p, relPath));
        }

        public static bool AnyKey(IEnumerable<string> patterns, string key)
        {
            return patterns != null && patterns.Any(p => !string.IsNullOrEmpty(p) && ToRegex(p).IsMatch(key));
        }
    }

    // RetroArch style key = "value" files (retroarch.cfg, overrides, .opt, .rmp).
    // Keeps every line (comments, #include, order) so writes only touch the changed keys.
    class KvFile
    {
        public List<string> Lines = new List<string>();
        public Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.Ordinal);
        public Dictionary<string, int> LineOf = new Dictionary<string, int>(StringComparer.Ordinal);
        public string NL = "\n";
        public bool Bom;

        static readonly Regex Rx = new Regex(@"^\s*([A-Za-z0-9_][A-Za-z0-9_\-\.\+]*)\s*=\s*(.*?)\s*$");

        public static KvFile Parse(byte[] data)
        {
            var f = new KvFile();
            if (data == null) return f;
            f.Bom = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF;
            string s = Util.StripBom(Util.Utf8.GetString(data));
            f.NL = s.Contains("\r\n") ? "\r\n" : "\n";
            var lines = s.Replace("\r\n", "\n").Split('\n').ToList();
            if (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);
            foreach (var l in lines) f.AddLine(l);
            return f;
        }

        void AddLine(string l)
        {
            var m = Rx.Match(l);
            if (m.Success)
            {
                string v = m.Groups[2].Value;
                if (v.Length >= 2 && v[0] == '"' && v[v.Length - 1] == '"') v = v.Substring(1, v.Length - 2);
                Values[m.Groups[1].Value] = v;
                LineOf[m.Groups[1].Value] = Lines.Count;
            }
            Lines.Add(l);
        }

        public void Set(string key, string value)
        {
            string line = key + " = \"" + value + "\"";
            int i;
            if (LineOf.TryGetValue(key, out i)) Lines[i] = line;
            else { LineOf[key] = Lines.Count; Lines.Add(line); }
            Values[key] = value;
        }

        public byte[] ToBytes()
        {
            string s = string.Join(NL, Lines) + (Lines.Count > 0 ? NL : "");
            var body = Util.Utf8.GetBytes(s);
            if (!Bom) return body;
            var r = new byte[body.Length + 3];
            r[0] = 0xEF; r[1] = 0xBB; r[2] = 0xBF;
            Buffer.BlockCopy(body, 0, r, 3, body.Length);
            return r;
        }
    }

    // Decides whether a key may travel between PC and PS5.
    class KeyFilter
    {
        readonly KeyFilterSettings s;
        public KeyFilter(KeyFilterSettings s) { this.s = s ?? new KeyFilterSettings(); }
        public bool AddMissingKeys { get { return s.AddMissingKeys; } }

        static readonly Regex NumBool = new Regex(@"^([+-]?\d+(\.\d+)?|true|false)$", RegexOptions.IgnoreCase);
        static readonly Regex Drive = new Regex(@"^[A-Za-z]:[\\/]");

        static bool IsBool(string v) { return v == "true" || v == "false"; }

        public static bool PathLike(string v)
        {
            if (string.IsNullOrEmpty(v)) return false;
            return v.StartsWith(":\\") || v.StartsWith(":/") || v.StartsWith("/") || v.StartsWith("\\\\") || v.StartsWith("~/") || Drive.IsMatch(v);
        }

        // Returns null when the key is shared, otherwise a short reason.
        public string PlatformReason(string key, string a, string b)
        {
            if (Glob.AnyKey(s.AlwaysShareKeys, key)) return null;
            if (Glob.AnyKey(s.PlatformKeys, key)) return "platform key";
            if (s.SkipPathLikeValues && (PathLike(a) || PathLike(b))) return "valore = percorso";
            // same key, different value format (e.g. "2" vs "true" between RetroArch versions): never copy
            if (a != null && b != null && a.Length > 0 && b.Length > 0 && IsBool(a) != IsBool(b)) return "formato diverso";
            if (s.TreatInputTextBindsAsPlatform && key.StartsWith("input_", StringComparison.OrdinalIgnoreCase))
            {
                bool na = a == null || NumBool.IsMatch(a), nb = b == null || NumBool.IsMatch(b);
                if (!na || !nb) return "tasto/bind";
            }
            return null;
        }

        public bool Shared(string key, string a, string b) { return PlatformReason(key, a, b) == null; }
    }

    enum Dir { None, ToPs5, ToPc, Merge }

    class KvMergeResult
    {
        public byte[] NewPc, NewPs5;              // null = that side does not change
        public List<string> ToPc = new List<string>(), ToPs5 = new List<string>(), Conflicts = new List<string>();
        public Dictionary<string, string> NewBasePc = new Dictionary<string, string>(), NewBasePs5 = new Dictionary<string, string>();
        public int SharedCommon, Skipped;
    }

    static class KvMerger
    {
        static string Short(string v) { v = v ?? ""; return v.Length > 40 ? v.Substring(0, 37) + "..." : v; }

        // Merges the shared keys of two key/value files.
        // policy Merge: 3-way per key against the last synced values (base); both changed -> newer side wins (conflict).
        // policy ToPs5 / ToPc: the given side receives every differing shared value of the other.
        // policy Merge: per key, compares each side with its own value at the last sync (basePc / basePs5):
        //   only one side changed -> it wins; neither changed -> both kept; both changed or no history -> newer file wins (conflict).
        // policy ToPs5 / ToPc: the given side receives every differing shared value of the other.
        public static KvMergeResult Merge(byte[] pcData, byte[] psData, Dictionary<string, string> basePc, Dictionary<string, string> basePs5,
            KeyFilter f, bool pcNewer, Dir policy)
        {
            var pc = KvFile.Parse(pcData);
            var ps = KvFile.Parse(psData);
            var res = new KvMergeResult();
            bool pcChanged = false, psChanged = false;
            if (basePs5 == null) basePs5 = basePc;   // state written by v1 had a single base

            foreach (var key in pc.Values.Keys.ToList())
            {
                string a = pc.Values[key], b;
                if (!ps.Values.TryGetValue(key, out b))
                {
                    if (!f.AddMissingKeys || !f.Shared(key, a, null) || policy == Dir.ToPc) continue;
                    ps.Set(key, a); psChanged = true; res.ToPs5.Add(key + " (nuova) = " + Short(a));
                    res.NewBasePc[key] = a; res.NewBasePs5[key] = a;
                    continue;
                }
                if (!f.Shared(key, a, b)) { res.Skipped++; continue; }
                res.SharedCommon++;
                if (a == b) { res.NewBasePc[key] = a; res.NewBasePs5[key] = b; continue; }

                string bpc = null, bps = null;
                if (basePc != null) basePc.TryGetValue(key, out bpc);
                if (basePs5 != null) basePs5.TryGetValue(key, out bps);
                bool pcCh = bpc == null || a != bpc, psCh = bps == null || b != bps;
                Dir d;
                bool conflict = false;
                if (policy == Dir.ToPs5) d = Dir.ToPs5;
                else if (policy == Dir.ToPc) d = Dir.ToPc;
                else if (!pcCh && !psCh) d = Dir.None;            // different on purpose since the last sync / baseline
                else if (pcCh && !psCh) d = Dir.ToPs5;
                else if (!pcCh && psCh) d = Dir.ToPc;
                else { conflict = true; d = pcNewer ? Dir.ToPs5 : Dir.ToPc; }

                if (d == Dir.None) { res.NewBasePc[key] = a; res.NewBasePs5[key] = b; continue; }
                if (d == Dir.ToPs5)
                {
                    ps.Set(key, a); psChanged = true;
                    res.ToPs5.Add(key + ": \"" + Short(b) + "\" \u2192 \"" + Short(a) + "\"");
                    res.NewBasePc[key] = a; res.NewBasePs5[key] = a;
                }
                else
                {
                    pc.Set(key, b); pcChanged = true;
                    res.ToPc.Add(key + ": \"" + Short(a) + "\" \u2192 \"" + Short(b) + "\"");
                    res.NewBasePc[key] = b; res.NewBasePs5[key] = b;
                }
                if (conflict) res.Conflicts.Add(key + " (PC \"" + Short(a) + "\" / PS5 \"" + Short(b) + "\", tengo " + (d == Dir.ToPs5 ? "PC" : "PS5") + ")");
            }
            if (f.AddMissingKeys && policy != Dir.ToPs5)
            {
                foreach (var key in ps.Values.Keys.ToList())
                {
                    if (pc.Values.ContainsKey(key)) continue;
                    string b = ps.Values[key];
                    if (!f.Shared(key, null, b)) continue;
                    pc.Set(key, b); pcChanged = true; res.ToPc.Add(key + " (nuova) = " + Short(b));
                    res.NewBasePc[key] = b; res.NewBasePs5[key] = b;
                }
            }
            if (pcChanged) res.NewPc = pc.ToBytes();
            if (psChanged) res.NewPs5 = ps.ToBytes();
            return res;
        }

        // Builds the content for a side that does not have the file yet: shared keys only.
        public static byte[] FilteredCopy(byte[] src, KeyFilter f, out int kept, out int dropped, out Dictionary<string, string> baseVals)
        {
            var k = KvFile.Parse(src);
            var outF = new KvFile { NL = k.NL, Bom = k.Bom };
            kept = 0; dropped = 0;
            baseVals = new Dictionary<string, string>();
            foreach (var kv in k.Values)
            {
                if (f.Shared(kv.Key, kv.Value, null)) { outF.Set(kv.Key, kv.Value); kept++; baseVals[kv.Key] = kv.Value; }
                else dropped++;
            }
            return kept == 0 ? null : outF.ToBytes();
        }
    }

    // ------------------------------------------------------------------ JSON (playlists)
    // Tiny ordered JSON model, enough for RetroArch .lpl files; output follows RetroArch's own layout.
    class JObj { public List<KeyValuePair<string, object>> Props = new List<KeyValuePair<string, object>>(); }
    class JNum { public string Raw; }

    static class MiniJson
    {
        public static object Parse(string s)
        {
            int i = 0;
            var v = Value(s, ref i);
            Ws(s, ref i);
            if (i != s.Length) throw new FormatException("JSON: testo extra alla posizione " + i);
            return v;
        }

        static void Ws(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        static object Value(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) throw new FormatException("JSON troncato");
            char c = s[i];
            if (c == '{')
            {
                var o = new JObj(); i++;
                Ws(s, ref i);
                if (s[i] == '}') { i++; return o; }
                while (true)
                {
                    Ws(s, ref i);
                    string k = Str(s, ref i);
                    Ws(s, ref i);
                    if (s[i] != ':') throw new FormatException("JSON: atteso ':'");
                    i++;
                    o.Props.Add(new KeyValuePair<string, object>(k, Value(s, ref i)));
                    Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; return o; }
                    throw new FormatException("JSON: atteso ',' o '}'");
                }
            }
            if (c == '[')
            {
                var a = new List<object>(); i++;
                Ws(s, ref i);
                if (s[i] == ']') { i++; return a; }
                while (true)
                {
                    a.Add(Value(s, ref i));
                    Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; return a; }
                    throw new FormatException("JSON: atteso ',' o ']'");
                }
            }
            if (c == '"') return Str(s, ref i);
            if (s.Substring(i).StartsWith("true")) { i += 4; return true; }
            if (s.Substring(i).StartsWith("false")) { i += 5; return false; }
            if (s.Substring(i).StartsWith("null")) { i += 4; return null; }
            int st = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (i == st) throw new FormatException("JSON: carattere inatteso '" + c + "'");
            return new JNum { Raw = s.Substring(st, i - st) };
        }

        static string Str(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException("JSON: attesa stringa");
            i++;
            var sb = new StringBuilder();
            while (true)
            {
                char c = s[i++];
                if (c == '"') break;
                if (c != '\\') { sb.Append(c); continue; }
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u': sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber)); i += 4; break;
                    default: sb.Append(e); break;
                }
            }
            return sb.ToString();
        }

        public static string Write(object v)
        {
            var sb = new StringBuilder();
            W(sb, v, 0);
            sb.Append('\n');
            return sb.ToString();
        }

        static void Ind(StringBuilder sb, int n) { sb.Append(' ', n * 2); }

        static void W(StringBuilder sb, object v, int ind)
        {
            if (v == null) { sb.Append("null"); return; }
            if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
            if (v is JNum) { sb.Append(((JNum)v).Raw); return; }
            var s = v as string;
            if (s != null) { Q(sb, s); return; }
            var o = v as JObj;
            if (o != null)
            {
                if (o.Props.Count == 0) { sb.Append("{}"); return; }
                sb.Append("{\n");
                for (int k = 0; k < o.Props.Count; k++)
                {
                    Ind(sb, ind + 1); Q(sb, o.Props[k].Key); sb.Append(": "); W(sb, o.Props[k].Value, ind + 1);
                    sb.Append(k < o.Props.Count - 1 ? ",\n" : "\n");
                }
                Ind(sb, ind); sb.Append('}');
                return;
            }
            var a = v as List<object>;
            if (a != null)
            {
                if (a.Count == 0) { sb.Append("[]"); return; }
                sb.Append("[\n");
                for (int k = 0; k < a.Count; k++)
                {
                    Ind(sb, ind + 1); W(sb, a[k], ind + 1);
                    sb.Append(k < a.Count - 1 ? ",\n" : "\n");
                }
                Ind(sb, ind); sb.Append(']');
            }
        }

        static void Q(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        public static object Get(JObj o, string key)
        {
            foreach (var p in o.Props) if (p.Key == key) return p.Value;
            return null;
        }

        public static void Set(JObj o, string key, object value)
        {
            for (int i = 0; i < o.Props.Count; i++)
                if (o.Props[i].Key == key) { o.Props[i] = new KeyValuePair<string, object>(key, value); return; }
            o.Props.Add(new KeyValuePair<string, object>(key, value));
        }
    }

    // ------------------------------------------------------------------ path translation
    // Translates absolute paths between PC (Windows) and PS5 (RetroArch view, e.g. /app0/...) using
    // the prefix list in settings (longest prefix wins). Core files also swap .dll <-> .so.
    class PathTranslator
    {
        readonly List<KeyValuePair<string, string>> maps = new List<KeyValuePair<string, string>>();
        readonly AppSettings s;

        public PathTranslator(AppSettings s)
        {
            this.s = s;
            foreach (var m in s.PathMappings)
            {
                if (m == null || string.IsNullOrEmpty(m.Pc) || string.IsNullOrEmpty(m.Ps5)) continue;
                string pc = m.Pc.Replace("{PC_ROOT}", s.PcRetroArchRoot).Replace('/', '\\').TrimEnd('\\');
                string ps = m.Ps5.Replace("{PS5_APP}", s.Ps5AppRoot).Replace('\\', '/').TrimEnd('/');
                maps.Add(new KeyValuePair<string, string>(pc, ps));
            }
        }

        static bool HasPrefix(string path, string prefix, char sep, StringComparison cmp)
        {
            if (!path.StartsWith(prefix, cmp)) return false;
            return path.Length == prefix.Length || path[prefix.Length] == sep;
        }

        static string SwapExt(string p, string from, string to)
        {
            if (!string.IsNullOrEmpty(from) && p.EndsWith(from, StringComparison.OrdinalIgnoreCase)) return p.Substring(0, p.Length - from.Length) + to;
            return p;
        }

        // Returns null when no mapping applies.
        public string ToPs5(string pc, bool isCore)
        {
            if (string.IsNullOrEmpty(pc)) return pc;
            string n = pc.Replace('/', '\\');
            var best = maps.Where(m => HasPrefix(n, m.Key, '\\', StringComparison.OrdinalIgnoreCase)).OrderByDescending(m => m.Key.Length).FirstOrDefault();
            if (best.Key == null) return null;
            string r = best.Value + n.Substring(best.Key.Length).Replace('\\', '/');
            return isCore ? SwapExt(r, s.PcCoreExtension, s.Ps5CoreExtension) : r;
        }

        public string ToPc(string ps, bool isCore)
        {
            if (string.IsNullOrEmpty(ps)) return ps;
            string n = ps.Replace('\\', '/');
            var best = maps.Where(m => HasPrefix(n, m.Value, '/', StringComparison.Ordinal)).OrderByDescending(m => m.Value.Length).FirstOrDefault();
            if (best.Key == null) return null;
            string r = best.Key + n.Substring(best.Value.Length).Replace('/', '\\');
            return isCore ? SwapExt(r, s.Ps5CoreExtension, s.PcCoreExtension) : r;
        }

        // "/app0/content/x" -> "/data/homebrew/RetroArch/content/x" (FTP path); null if outside the app root.
        public string AppToFtp(string appPath)
        {
            string app = s.Ps5AppRoot.TrimEnd('/');
            if (!HasPrefix(appPath, app, '/', StringComparison.Ordinal)) return null;
            return Util.RemoteJoin(s.Ps5RetroArchRoot, appPath.Substring(app.Length));
        }
    }

    // Converts a playlist between the PC and PS5 flavour.
    static class PlaylistConverter
    {
        static readonly string[] PathKeys = { "path", "scan_content_dir", "scan_dat_file_path" };
        static readonly string[] CoreKeys = { "core_path", "default_core_path" };

        public static int ItemCount(string text)
        {
            try { var r = MiniJson.Parse(Util.StripBom(text)) as JObj; var l = r == null ? null : MiniJson.Get(r, "items") as List<object>; return l == null ? 0 : l.Count; }
            catch { return -1; }
        }

        public static string Normalize(string text)
        {
            try { return MiniJson.Write(MiniJson.Parse(Util.StripBom(text))); }
            catch { return text.Replace("\r\n", "\n"); }
        }

        // exists: checks whether a content file exists on the target side (null = do not check).
        // targetText: the playlist currently on the destination side (or null). When present its header
        // (default core, scan settings, sort mode...) is kept and only the converted "items" replace its own.
        public static string Convert(string text, bool toPs5, PathTranslator tr, Func<string, bool> exists, List<string> notes, string targetText)
        {
            var root = MiniJson.Parse(Util.StripBom(text)) as JObj;
            if (root == null) throw new FormatException("invalid playlist");
            Func<string, bool, string> conv = (p, core) => toPs5 ? tr.ToPs5(p, core) : tr.ToPc(p, core);
            JObj target = null;
            if (targetText != null) { try { target = MiniJson.Parse(Util.StripBom(targetText)) as JObj; } catch { target = null; } }
            if (target == null) TranslateObj(root, conv, notes, false);
            var items = MiniJson.Get(root, "items") as List<object>;
            if (items != null)
            {
                var keep = new List<object>();
                foreach (var o in items)
                {
                    var it = o as JObj;
                    if (it == null) { keep.Add(o); continue; }
                    string orig = MiniJson.Get(it, "path") as string;
                    bool untranslatable = TranslateObj(it, conv, notes, true);
                    string p = MiniJson.Get(it, "path") as string;
                    string label = (MiniJson.Get(it, "label") as string) ?? orig;
                    if (untranslatable) { notes.Add("entry with no path match, skipped: " + label); continue; }
                    if (exists != null && !string.IsNullOrEmpty(p))
                    {
                        string file = p;
                        int hash = file.IndexOf('#');
                        if (hash > 0) file = file.Substring(0, hash);
                        if (!exists(file)) { notes.Add("content missing on the other side, entry skipped: " + label); continue; }
                    }
                    keep.Add(it);
                }
                MiniJson.Set(root, "items", keep);
            }
            if (target != null)
            {
                MiniJson.Set(target, "items", MiniJson.Get(root, "items") ?? new List<object>());
                return MiniJson.Write(target);
            }
            return MiniJson.Write(root);
        }

        // Returns true when the item's content path could not be translated.
        static bool TranslateObj(JObj o, Func<string, bool, string> conv, List<string> notes, bool isItem)
        {
            bool bad = false;
            for (int i = 0; i < o.Props.Count; i++)
            {
                var p = o.Props[i];
                var v = p.Value as string;
                if (string.IsNullOrEmpty(v) || v == "DETECT") continue;
                bool core = CoreKeys.Contains(p.Key);
                if (!core && !PathKeys.Contains(p.Key)) continue;
                string t = conv(v, core);
                if (t == null)
                {
                    if (core) t = isItem ? "DETECT" : "";
                    else if (isItem && p.Key == "path") { bad = true; t = v; }
                    else { t = ""; if (notes != null) notes.Add(p.Key + " senza corrispondenza, svuotato"); }
                }
                o.Props[i] = new KeyValuePair<string, object>(p.Key, t);
            }
            return bad;
        }
    }

    // ------------------------------------------------------------------ state
    public class ItemState
    {
        public string LocalSha, RemoteSha;
        public long LocalSize, RemoteSize;
        public long LocalMtime, RemoteMtime;   // UTC ticks
        public long Synced;
        public Dictionary<string, string> Base;    // PC shared values at the last sync / baseline (key/value merge only)
        public Dictionary<string, string> BasePs5; // PS5 shared values at the last sync / baseline
    }

    public class StateFile
    {
        public int Version = 1;
        public Dictionary<string, ItemState> Items = new Dictionary<string, ItemState>(StringComparer.OrdinalIgnoreCase);
    }

    class StateStore
    {
        public StateFile Data;
        readonly string path;

        public StateStore(string path)
        {
            this.path = path;
            Data = SettingsStore.LoadJson<StateFile>(path);
            if (Data.Items == null) Data.Items = new Dictionary<string, ItemState>(StringComparer.OrdinalIgnoreCase);
            else Data.Items = new Dictionary<string, ItemState>(Data.Items, StringComparer.OrdinalIgnoreCase);
        }

        public ItemState Get(string key) { ItemState s; return Data.Items.TryGetValue(key, out s) ? s : null; }
        public void Set(string key, ItemState s) { Data.Items[key] = s; }
        public void Save() { SettingsStore.SaveJson(path, Data); }
    }

    // ------------------------------------------------------------------ backups
    // Every overwritten files (both sides) is copied to <root>\yyyyMMdd-HHmmss\{PC|PS5}\<relative path>.
    class BackupManager
    {
        readonly string root;
        readonly int keep;
        readonly Action<string> log;
        string session;
        public int Count;

        public BackupManager(string root, int keep, Action<string> log) { this.root = root; this.keep = keep; this.log = log; }

        public string SessionDir
        {
            get
            {
                if (session == null)
                {
                    session = Path.Combine(root, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                    Directory.CreateDirectory(session);
                }
                return session;
            }
        }

        public void SaveBytes(string side, string rel, byte[] data)
        {
            string dest = Path.Combine(SessionDir, side, rel.Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            File.WriteAllBytes(dest, data);
            Count++;
        }

        public void SaveLocal(string side, string rel, string file)
        {
            string dest = Path.Combine(SessionDir, side, rel.Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            File.Copy(file, dest, true);
            Count++;
        }

        public void Prune()
        {
            if (!Directory.Exists(root)) return;
            var rx = new Regex(@"^\d{8}-\d{6}$");
            var old = new DirectoryInfo(root).GetDirectories().Where(d => rx.IsMatch(d.Name)).OrderByDescending(d => d.Name).Skip(keep).ToList();
            foreach (var d in old)
            {
                try { d.Delete(true); if (log != null) log("Backup vecchio rimosso: " + d.Name); } catch { }
            }
        }
    }

    // Local writes go through here so dry-run can block them in one place.
    static class LocalFs
    {
        public static bool ReadOnly;

        public static void Write(string path, byte[] data, DateTime? mtimeUtc)
        {
            if (ReadOnly) throw new InvalidOperationException("Preview mode: local write blocked (" + path + ")");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".retrosync.tmp";
            File.WriteAllBytes(tmp, data);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            if (mtimeUtc.HasValue) { try { File.SetLastWriteTimeUtc(path, mtimeUtc.Value); } catch { } }
        }
    }
}
