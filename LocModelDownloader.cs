using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: AssemblyTitle("LocModel 模型下载器")]
[assembly: AssemblyProduct("LocModel Downloader")]
[assembly: AssemblyCompany("大圣Ai自习室")]
[assembly: AssemblyDescription("LocModel 模型/配方下载器（aria2 多线程、断点续传、批量队列）")]
[assembly: AssemblyVersion("1.1.7.0")]
[assembly: AssemblyFileVersion("1.1.7.0")]

namespace LocModelDownloader
{
    public enum Source { HF = 0, MS = 1, Civitai = 2 }

    public class AuthNeeded : Exception
    {
        public Source Kind;
        public AuthNeeded(Source s, string msg) : base(msg) { Kind = s; }
    }

    public static class Secrets
    {
        public static string Hf = "";
        public static string Ms = "";
        public static string Civitai = "";

        static string FilePath()
        {
            string dir = Core.DataDir();
            if (dir == null) dir = AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(dir, "secrets.bin");
        }

        public static void Load()
        {
            try
            {
                string p = FilePath();
                if (!File.Exists(p)) return;
                byte[] raw = System.Security.Cryptography.ProtectedData.Unprotect(
                    File.ReadAllBytes(p), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                string text = Encoding.UTF8.GetString(raw);
                foreach (string line in text.Split('\n'))
                {
                    int i = line.IndexOf('=');
                    if (i <= 0) continue;
                    string k = line.Substring(0, i).Trim();
                    string v = line.Substring(i + 1).Trim();
                    if (k == "hf") Hf = v;
                    if (k == "ms") Ms = v;
                    if (k == "civitai") Civitai = v;
                }
            }
            catch { }
        }

        public static bool Save()
        {
            try
            {
                Hf = Core.SanitizeToken(Hf);
                Ms = Core.SanitizeToken(Ms);
                Civitai = Core.SanitizeToken(Civitai);
                byte[] raw = Encoding.UTF8.GetBytes("hf=" + Hf + "\nms=" + Ms + "\ncivitai=" + Civitai + "\n");
                byte[] enc = System.Security.Cryptography.ProtectedData.Protect(
                    raw, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                string p = FilePath();
                string tmp = p + ".tmp";
                File.WriteAllBytes(tmp, enc);
                if (File.Exists(p))
                {
                    try { File.Replace(tmp, p, null); }
                    catch { File.Copy(tmp, p, true); try { File.Delete(tmp); } catch { } }
                }
                else File.Move(tmp, p);
                return true;
            }
            catch (Exception ex)
            {
                Core.Log("保存令牌失败: " + ex.Message);
                return false;
            }
        }
    }

    static class Core
    {
        public const string DefaultMirror = "https://hf-mirror.net";
        public const string MsBase = "https://modelscope.cn";
        public const string DefaultModelsDir = @"D:\LocModel\models";

        static int connections = 16;
        public static int Connections { get { return connections; } }

        static string proxy = "";
        public static string Proxy { get { return proxy; } }

        // 下载后的校验强度：off 不校验 / size 只比大小（默认）/ hash 大小 + sha256
        static string verifyMode = "size";
        public static string VerifyMode { get { return verifyMode; } }

        public static void LoadConfig()
        {
            try
            {
                string env = Environment.GetEnvironmentVariable("LOCMODEL_DL_CONNECTIONS");
                int n;
                if (!string.IsNullOrEmpty(env) && int.TryParse(env, out n) && n >= 1 && n <= 64) connections = n;
                string envv = Environment.GetEnvironmentVariable("LOCMODEL_DL_VERIFY");
                if (!string.IsNullOrEmpty(envv))
                {
                    string ev = envv.Trim().ToLowerInvariant();
                    if (ev == "off" || ev == "size" || ev == "hash") verifyMode = ev;
                }
                string f = Path.Combine(DataDir() ?? AppDomain.CurrentDomain.BaseDirectory, "config.txt");
                if (File.Exists(f))
                {
                    foreach (string line in File.ReadAllLines(f, Encoding.UTF8))
                    {
                        string t = line.Trim();
                        if (t.Length == 0 || t.StartsWith("#")) continue;
                        int i = t.IndexOf('=');
                        if (i <= 0) continue;
                        string key = t.Substring(0, i).Trim();
                        string val = t.Substring(i + 1).Trim();
                        if (key.Equals("connections", StringComparison.OrdinalIgnoreCase))
                            if (int.TryParse(val, out n) && n >= 1 && n <= 64) connections = n;
                        if (key.Equals("proxy", StringComparison.OrdinalIgnoreCase)) proxy = val;
                        if (key.Equals("verify", StringComparison.OrdinalIgnoreCase))
                        {
                            string v = val.Trim().ToLowerInvariant();
                            if (v == "off" || v == "size" || v == "hash") verifyMode = v;
                        }
                    }
                }
                if (!string.IsNullOrEmpty(proxy))
                    WebRequest.DefaultWebProxy = new WebProxy(proxy);
            }
            catch { }
        }

        public class FileEntry
        {
            public string Name;
            public long Size;
            public string Repo;
            public string Url;
            public string SaveAs;
            public string Hash;   // sha256（HF 的 lfs.sha256 / 魔塔的 Sha256 / C站的 hashes.SHA256），站点没给就是 null
            public override string ToString() { return Name + "    (" + Fmt(Size) + ")"; }
        }

        public class Component
        {
            public string Repo;
            public string Path;
            public string SaveAs;
        }

        public class Partial
        {
            public string Dir;
            public string FileName;
            public string Repo;
            public string SubPath;
            public string SourceNote;
        }

        public class Recipe
        {
            public string Id;
            public string Title;
            public List<string> QuantLabels = new List<string>();
            public List<List<Component>> Quants = new List<List<Component>>();
            public List<Component> Fixed = new List<Component>();
            public override string ToString() { return Title + "   (" + Id + ")"; }
        }

        public static string Fmt(long bytes)
        {
            double b = bytes;
            if (b >= 1024.0 * 1024 * 1024) return (b / (1024.0 * 1024 * 1024)).ToString("0.00") + " GB";
            if (b >= 1024.0 * 1024) return (b / (1024.0 * 1024)).ToString("0.0") + " MB";
            if (b >= 1024.0) return (b / 1024.0).ToString("0") + " KB";
            return bytes + " B";
        }

        public static string NormalizeHash(string h)
        {
            if (string.IsNullOrEmpty(h)) return null;
            string s = h.Trim().ToLowerInvariant();
            if (s.StartsWith("sha256:")) s = s.Substring(7).Trim();
            if (s.Length != 64) return null;
            foreach (char c in s)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return null;
            return s;
        }

        // 分块算 sha256，避免大模型一次性读进内存；progress 是 0-100
        public static string Sha256File(string path, Action<int> progress)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024))
            using (SHA256 sha = SHA256.Create())
            {
                byte[] buf = new byte[1024 * 1024];
                long total = fs.Length, done = 0;
                int lastPct = -1, n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    sha.TransformBlock(buf, 0, n, null, 0);
                    done += n;
                    if (progress != null && total > 0)
                    {
                        int pct = (int)(done * 100 / total);
                        if (pct != lastPct) { lastPct = pct; progress(pct); }
                    }
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                StringBuilder sb = new StringBuilder(64);
                foreach (byte b in sha.Hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        // 校验单个文件：null = 通过；否则返回原因。actualSha 总会带回（文件不存在时为 null）
        public static string VerifyFile(string path, long expectSize, string expectHash, Action<int> progress, out string actualSha)
        {
            actualSha = null;
            try
            {
                if (!File.Exists(path)) return "文件不存在";
                long actual = new FileInfo(path).Length;
                if (actual <= 0) return "文件是 0 字节，不完整";
                string eh = NormalizeHash(expectHash);
                if (expectSize <= 0 && eh == null) return "无法比对：站点没给大小和哈希";
                // 先比大小：大小都不对就没必要再花几十秒算哈希
                if (expectSize > 0 && actual != expectSize)
                    return "大小不符：应为 " + Fmt(expectSize) + "（" + expectSize + " 字节），实际 " + Fmt(actual) + "（" + actual + " 字节）";
                actualSha = Sha256File(path, progress);
                if (eh != null && !string.Equals(actualSha, eh, StringComparison.OrdinalIgnoreCase))
                    return "sha256 不符：应为 " + eh.Substring(0, 12) + "…，实际 " + actualSha.Substring(0, 12) + "…";
                return null;
            }
            catch (Exception ex) { return "读取失败：" + ex.Message; }
        }

        // 按链接域名判断来源（用户经常直接粘链接，不该让他自己去切下拉）
        public static bool TryDetectSource(string input, out Source src)
        {
            src = Source.HF;
            if (string.IsNullOrEmpty(input)) return false;
            string t = input.Trim().ToLowerInvariant();
            if (!t.StartsWith("http")) return false;
            if (t.Contains("modelscope.cn") || t.Contains("modelscope.com")) { src = Source.MS; return true; }
            if (t.Contains("civitai.com")) { src = Source.Civitai; return true; }
            if (t.Contains("huggingface.co") || t.Contains("hf-mirror.net") || t.Contains("hf-mirror.com")) { src = Source.HF; return true; }
            return false;
        }

        // 从「文件页」链接里取出具体文件名，列表加载后自动勾上
        public static string FileFromUrl(string input)
        {
            if (string.IsNullOrEmpty(input)) return null;
            string t = input.Trim();
            int q = t.IndexOfAny(new char[] { '?', '#' });
            if (q >= 0) t = t.Substring(0, q);
            Match m = Regex.Match(t, @"/(?:blob|tree|resolve)/[^/]+/(.+)$", RegexOptions.IgnoreCase);
            if (m.Success) return Uri.UnescapeDataString(m.Groups[1].Value);
            m = Regex.Match(t, @"/file/view/[^/]+/(.+)$", RegexOptions.IgnoreCase);
            if (m.Success) return Uri.UnescapeDataString(m.Groups[1].Value);
            return null;
        }

        // 用户常把「文件名」或「网页路径」当仓库名粘进来，单独识别一下
        public static bool LooksLikeModelFile(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            string t = s.Trim().ToLowerInvariant();
            string[] exts = { ".safetensors", ".gguf", ".bin", ".pt", ".pth", ".ckpt", ".onnx", ".pkl" };
            foreach (string e in exts) if (t.EndsWith(e)) return true;
            if (t.Contains("/file/view/") || t.Contains("\\file\\view\\")) return true;
            return false;
        }

        // 统一入口：把用户输入（仓库名 / 链接 / ms: 前缀）解析成「来源 + 仓库 + 文件提示」。
        // 主界面、批量队列、校验窗、命令行都必须走这里，避免同一段链接在不同入口被当成不同站点。
        public static void ParseRepoInput(string raw, Source current, out Source src, out string repo, out string fileHint)
        {
            src = current;
            repo = (raw ?? "").Trim();
            fileHint = null;

            if (repo.StartsWith("ms:", StringComparison.OrdinalIgnoreCase)) { src = Source.MS; repo = repo.Substring(3).Trim(); }
            else if (repo.StartsWith("cv:", StringComparison.OrdinalIgnoreCase)) { src = Source.Civitai; repo = repo.Substring(4).Trim(); }
            else if (repo.StartsWith("hf:", StringComparison.OrdinalIgnoreCase)) { src = Source.HF; repo = repo.Substring(3).Trim(); }

            Source det;
            if (TryDetectSource(repo, out det)) src = det;   // 链接域名优先于下拉框

            fileHint = FileFromUrl(repo);
            repo = (src == Source.Civitai) ? repo.Trim() : NormRepo(repo);
        }

        public static string NormRepo(string input)
        {
            if (input == null) return "";
            string s = input.Trim();
            s = s.Replace("https://", "").Replace("http://", "");
            int q = s.IndexOfAny(new char[] { '?', '#' });
            if (q >= 0) s = s.Substring(0, q);
            string[] hosts = { "hf-mirror.net/", "hf-mirror.com/", "huggingface.co/", "modelscope.cn/models/", "modelscope.cn/" };
            foreach (string h in hosts)
            {
                if (s.StartsWith(h, StringComparison.OrdinalIgnoreCase)) { s = s.Substring(h.Length); break; }
            }
            s = s.Trim('/');
            // 粘贴的是页面链接时，后面还会带 tree/main、blob/main/xx、files、resolve/main/xx 等路径，只保留 owner/repo
            string[] parts = s.Split('/');
            if (parts.Length > 2) s = parts[0] + "/" + parts[1];
            return s;
        }

        public static string FolderFor(string repo)
        {
            string r = NormRepo(repo);
            int i = r.IndexOf('/');
            if (i <= 0) return r.Replace("/", "__");
            return r.Substring(0, i) + "__" + r.Substring(i + 1).Replace("/", "__");
        }

        static string CacheFile()
        {
            try
            {
                string dir = DataDir();
                if (dir == null) return null;
                return Path.Combine(dir, "models_dir.txt");
            }
            catch { return null; }
        }

        public static void CacheModelsDir(string dir)
        {
            try
            {
                string f = CacheFile();
                if (f != null && !string.IsNullOrEmpty(dir)) File.WriteAllText(f, dir, new UTF8Encoding(false));
            }
            catch { }
        }

        static string SettingsModelsDir()
        {
            try
            {
                string settings = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"LocModel\settings.json");
                if (File.Exists(settings))
                {
                    string text = File.ReadAllText(settings, Encoding.UTF8);
                    Match m = Regex.Match(text, "\"models_dir\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                    if (m.Success)
                    {
                        string dir = m.Groups[1].Value.Replace(@"\\", @"\");
                        if (dir.Length > 2) return dir;
                    }
                }
            }
            catch { }
            return null;
        }

        public static bool HasSettingsModelsDir()
        {
            string d = SettingsModelsDir();
            return d != null && Directory.Exists(d);
        }

        static string[] CommonRelatives()
        {
            return new string[]
            {
                @"LocModel\models",
                @"Apps\LocModel\models",
                @"Programs\LocModel\models",
                @"Tools\LocModel\models",
                @"Software\LocModel\models",
                @"AI\LocModel\models",
                @"Program Files\LocModel\models",
                @"Program Files (x86)\LocModel\models"
            };
        }

        public static string DetectModelsDir()
        {
            string fromSettings = SettingsModelsDir();
            if (fromSettings != null && Directory.Exists(fromSettings)) return fromSettings;

            string cached = null;
            try
            {
                string f = CacheFile();
                if (f != null && File.Exists(f)) cached = File.ReadAllText(f).Trim();
            }
            catch { }
            if (!string.IsNullOrEmpty(cached) && cached.Length > 3 && Directory.Exists(cached)) return cached;

            try
            {
                foreach (DriveInfo di in DriveInfo.GetDrives())
                {
                    if (!di.IsReady) continue;
                    if (di.DriveType != DriveType.Fixed && di.DriveType != DriveType.Removable) continue;
                    foreach (string rel in CommonRelatives())
                    {
                        string p = Path.Combine(di.RootDirectory.FullName, rel);
                        if (Directory.Exists(p)) { CacheModelsDir(p); return p; }
                    }
                }
            }
            catch { }

            foreach (string rel in CommonRelatives())
            {
                string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), rel);
                if (Directory.Exists(p)) { CacheModelsDir(p); return p; }
            }

            if (fromSettings != null) return fromSettings;
            return DefaultModelsDir;
        }

        public static string ScanForModels(int secondsBudget)
        {
            DateTime deadline = DateTime.Now.AddSeconds(secondsBudget);
            string[] skip = { "windows", "$recycle.bin", "system volume information", "node_modules", ".git",
                              "appdata", "recovery", "perflogs", "msocache" };
            try
            {
                foreach (DriveInfo di in DriveInfo.GetDrives())
                {
                    if (!di.IsReady) continue;
                    if (di.DriveType != DriveType.Fixed) continue;
                    Queue<KeyValuePair<string, int>> q = new Queue<KeyValuePair<string, int>>();
                    q.Enqueue(new KeyValuePair<string, int>(di.RootDirectory.FullName, 0));
                    while (q.Count > 0)
                    {
                        if (DateTime.Now > deadline) return null;
                        KeyValuePair<string, int> cur = q.Dequeue();
                        string dir = cur.Key;
                        int depth = cur.Value;

                        if (string.Equals(Path.GetFileName(dir.TrimEnd('\\')), "LocModel", StringComparison.OrdinalIgnoreCase))
                        {
                            string m = Path.Combine(dir, "models");
                            if (Directory.Exists(m)) { CacheModelsDir(m); return m; }
                        }
                        if (depth >= 4) continue;

                        string[] subs;
                        try { subs = Directory.GetDirectories(dir); }
                        catch { continue; }
                        foreach (string s in subs)
                        {
                            string nm = Path.GetFileName(s);
                            bool bad = false;
                            foreach (string k in skip) if (string.Equals(nm, k, StringComparison.OrdinalIgnoreCase)) { bad = true; break; }
                            if (bad) continue;
                            try
                            {
                                if ((File.GetAttributes(s) & FileAttributes.ReparsePoint) != 0) continue;
                            }
                            catch { continue; }
                            q.Enqueue(new KeyValuePair<string, int>(s, depth + 1));
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        public static string RecipeFolder(string modelsDir, string id)
        {
            return Path.Combine(modelsDir, "image", id);
        }

        public static string CommonFolder(string modelsDir)
        {
            return Path.Combine(modelsDir, "image", "common");
        }

        public static JobItem BuildRecipeJob(Recipe rec, int qi, string modelsDir)
        {
            JobItem j = new JobItem();
            j.Kind = "recipe";
            j.RecipeId = rec.Id;
            j.Quant = rec.QuantLabels[qi];
            string rdir = RecipeFolder(modelsDir, rec.Id);
            string cdir = CommonFolder(modelsDir);
            foreach (Component c in rec.Quants[qi])
            {
                JobFile f = new JobFile();
                f.Repo = c.Repo; f.Path = c.Path; f.SaveAs = c.SaveAs; f.Dir = rdir;
                j.Files.Add(f);
            }
            foreach (Component c in rec.Fixed)
            {
                JobFile f = new JobFile();
                f.Repo = c.Repo; f.Path = c.Path; f.SaveAs = c.SaveAs; f.Dir = cdir;
                j.Files.Add(f);
            }
            return j;
        }

        public class JobFile
        {
            public string Repo;
            public string Path;
            public string SaveAs;
            public string Dir;
            public string Url;
            public long Size;      // 站点给的期望大小（0 = 未知）
            public string Hash;    // 站点给的 sha256（null = 没给）
        }

        public class JobItem
        {
            public string Kind = "repo";      // repo / recipe
            public string Src = "hf";         // hf / ms / cv
            public string Repo = "";          // repo kind
            public string RecipeId = "";      // recipe kind
            public string Quant = "";
            public List<JobFile> Files = new List<JobFile>();
            public string Status = "pending"; // pending / running / done / failed / cancelled / paused
            public long TotalSize;
            public string Title
            {
                get
                {
                    string tag = Src == "ms" ? "魔塔 " : (Src == "cv" ? "C站 " : "");
                    if (Kind == "recipe") return "配方 " + RecipeId + " " + Quant;
                    if (Files.Count == 1) return tag + Repo + " / " + Files[0].SaveAs;
                    return tag + Repo + "（" + Files.Count + " 个文件）";
                }
            }
            public override string ToString()
            {
                string st = Status == "pending" ? "待下载" : Status == "running" ? "下载中" :
                            Status == "done" ? "完成" : Status == "failed" ? "失败" :
                            Status == "cancelled" ? "已取消" : Status == "paused" ? "已暂停" : Status;
                return Title + "    [" + st + "  " + Fmt(TotalSize) + "]";
            }
        }

        public static void Log(string msg)
        {
            try
            {
                string dir = DataDir();
                if (dir == null) return;
                File.AppendAllText(Path.Combine(dir, "debug.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + Redact(msg) + "\r\n", new UTF8Encoding(false));
            }
            catch { }
        }

        // 日志里不要留下令牌/带 token 的 URL
        public static string Redact(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            string r = Regex.Replace(s, @"(token=)[^&\s""']+", "$1***", RegexOptions.IgnoreCase);
            r = Regex.Replace(r, @"(Bearer\s+)[A-Za-z0-9_\-\.]{12,}", "$1***", RegexOptions.IgnoreCase);
            return r;
        }

        public static string SanitizeToken(string t)
        {
            if (string.IsNullOrEmpty(t)) return "";
            return t.Replace("\"", "").Replace("\r", "").Replace("\n", "").Trim();
        }

        public static string DataDir()        {
            try
            {
                string env = Environment.GetEnvironmentVariable("LOCMODEL_DL_HOME");
                if (!string.IsNullOrEmpty(env)) { Directory.CreateDirectory(env); return env; }
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                if (File.Exists(Path.Combine(baseDir, "portable.txt")))
                {
                    string p = Path.Combine(baseDir, "data");
                    Directory.CreateDirectory(p);
                    return p;
                }
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocModelDownloader");
                Directory.CreateDirectory(dir);
                return dir;
            }
            catch
            {
                try { return AppDomain.CurrentDomain.BaseDirectory; }
                catch { return null; }
            }
        }

        public static string QueueFile()
        {
            try
            {
                string dir = DataDir();
                if (dir == null) return null;
                return Path.Combine(dir, "queue.json");
            }
            catch { return null; }
        }

        public static bool SameJob(JobItem a, JobItem b)
        {
            if (a == null || b == null) return false;
            if ((a.Src ?? "") != (b.Src ?? "") || (a.Kind ?? "") != (b.Kind ?? "") || (a.Repo ?? "") != (b.Repo ?? "") ||
                (a.RecipeId ?? "") != (b.RecipeId ?? "") || (a.Quant ?? "") != (b.Quant ?? "")) return false;
            if (a.Files == null || b.Files == null) return a.Files == b.Files;
            if (a.Files.Count != b.Files.Count) return false;
            List<string> ka = new List<string>(), kb = new List<string>();
            foreach (JobFile f in a.Files) ka.Add(f.Repo + "|" + f.Path + "|" + f.SaveAs);
            foreach (JobFile f in b.Files) kb.Add(f.Repo + "|" + f.Path + "|" + f.SaveAs);
            ka.Sort(StringComparer.OrdinalIgnoreCase);
            kb.Sort(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < ka.Count; i++)
                if (!string.Equals(ka[i], kb[i], StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        public static void SaveQueue(List<JobItem> jobs)
        {
            try
            {
                string f = QueueFile();
                if (f == null) return;
                List<object> arr = new List<object>();
                foreach (JobItem j in jobs)
                {
                    Dictionary<string, object> d = new Dictionary<string, object>();
                    d["kind"] = j.Kind;
                    d["src"] = j.Src;
                    d["repo"] = j.Repo;
                    d["recipeId"] = j.RecipeId;
                    d["quant"] = j.Quant;
                    d["status"] = j.Status;
                    d["totalSize"] = j.TotalSize;
                    List<object> fs = new List<object>();
                    foreach (JobFile f2 in j.Files)
                    {
                        Dictionary<string, object> o = new Dictionary<string, object>();
                        o["repo"] = f2.Repo; o["path"] = f2.Path; o["saveAs"] = f2.SaveAs; o["dir"] = f2.Dir; o["url"] = f2.Url;
                        o["size"] = f2.Size; o["hash"] = f2.Hash;
                        fs.Add(o);
                    }
                    d["files"] = fs;
                    arr.Add(d);
                }
                JavaScriptSerializer js = new JavaScriptSerializer();
                string json = js.Serialize(arr);
                string tmp = f + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                if (File.Exists(f))
                {
                    try { File.Replace(tmp, f, null); }
                    catch { File.Copy(tmp, f, true); try { File.Delete(tmp); } catch { } }
                }
                else File.Move(tmp, f);
            }
            catch (Exception ex) { Log("保存队列失败: " + ex.Message); }
        }

        public static List<JobItem> LoadQueue()
        {
            List<JobItem> list = new List<JobItem>();
            string f = null;
            try
            {
                f = QueueFile();
                if (f == null || !File.Exists(f)) return list;
                JavaScriptSerializer js = new JavaScriptSerializer();
                js.MaxJsonLength = 64 * 1024 * 1024;
                System.Collections.IEnumerable arr = js.DeserializeObject(File.ReadAllText(f, Encoding.UTF8)) as System.Collections.IEnumerable;
                if (arr == null) return list;
                foreach (object o in arr)
                {
                    Dictionary<string, object> d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    JobItem j = new JobItem();
                    j.Kind = d.ContainsKey("kind") ? Convert.ToString(d["kind"]) : "repo";
                    j.Src = d.ContainsKey("src") ? Convert.ToString(d["src"]) : "hf";
                    j.Repo = d.ContainsKey("repo") ? Convert.ToString(d["repo"]) : "";
                    j.RecipeId = d.ContainsKey("recipeId") ? Convert.ToString(d["recipeId"]) : "";
                    j.Quant = d.ContainsKey("quant") ? Convert.ToString(d["quant"]) : "";
                    j.Status = d.ContainsKey("status") ? Convert.ToString(d["status"]) : "pending";
                    // 上次进程被杀 / 断电时，任务会停在 running；重开时没有下载在跑，纠正为 paused
                    if (j.Status == "running") j.Status = "paused";
                    long ln;
                    if (d.ContainsKey("totalSize") && long.TryParse(Convert.ToString(d["totalSize"]), out ln)) j.TotalSize = ln;
                    if (d.ContainsKey("files") && d["files"] != null)
                    {
                        System.Collections.IEnumerable fs = d["files"] as System.Collections.IEnumerable;
                        if (fs != null)
                        foreach (object fo in fs)
                        {
                            Dictionary<string, object> fd = fo as Dictionary<string, object>;
                            if (fd == null) continue;
                            JobFile jf = new JobFile();
                            jf.Repo = fd.ContainsKey("repo") ? Convert.ToString(fd["repo"]) : "";
                            jf.Path = fd.ContainsKey("path") ? Convert.ToString(fd["path"]) : "";
                            jf.SaveAs = fd.ContainsKey("saveAs") ? Convert.ToString(fd["saveAs"]) : "";
                            jf.Dir = fd.ContainsKey("dir") ? Convert.ToString(fd["dir"]) : "";
                            jf.Url = fd.ContainsKey("url") ? Convert.ToString(fd["url"]) : null;
                            long fsz;
                            if (fd.ContainsKey("size") && long.TryParse(Convert.ToString(fd["size"]), out fsz)) jf.Size = fsz;
                            jf.Hash = fd.ContainsKey("hash") ? Convert.ToString(fd["hash"]) : null;
                            j.Files.Add(jf);
                        }
                    }
                    if (j.Files.Count > 0) list.Add(j);
                }
            }
            catch (Exception ex)
            {
                try
                {
                    if (f != null && File.Exists(f)) File.Copy(f, f + ".bad", true);
                    Log("队列文件解析失败，已备份为 queue.json.bad：" + ex.Message);
                }
                catch { }
            }
            return list;
        }

        public static long FreeSpace(string dir)
        {
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(dir));
                foreach (DriveInfo di in DriveInfo.GetDrives())
                    if (di.IsReady && string.Equals(di.RootDirectory.FullName, root, StringComparison.OrdinalIgnoreCase))
                        return di.AvailableFreeSpace;
            }
            catch { }
            return -1;
        }

        public static JobItem ResolveRepoJob(Source src, string mirror, string repo, string onlyFile, string modelsDir)
        {
            JobItem j = new JobItem();
            j.Kind = "repo";
            j.Src = src == Source.MS ? "ms" : (src == Source.Civitai ? "cv" : "hf");
            if (src == Source.Civitai)
            {
                repo = ResolveCivitaiVersion(repo);   // 支持粘模型主页链接，统一折算成版本 ID
                j.Repo = repo;
            }
            else j.Repo = NormRepo(repo);
            List<FileEntry> all = FetchFiles(src, mirror, repo);
            List<FileEntry> pick = new List<FileEntry>();
            if (src == Source.Civitai)
            {
                foreach (FileEntry f in all) pick.Add(f);
            }
            else if (!string.IsNullOrEmpty(onlyFile))
            {
                foreach (FileEntry f in all) if (f.Name == onlyFile) pick.Add(f);
            }
            else
            {
                string[] prefer = { "Q8_0", "Q6_K", "Q5_K_M", "Q4_K_M", "Q4_K", "Q4_0", "f16" };
                FileEntry chosen = null;
                foreach (string p in prefer)
                {
                    foreach (FileEntry f in all)
                        if (f.Name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) &&
                            f.Name.IndexOf("mmproj", StringComparison.OrdinalIgnoreCase) < 0 &&
                            f.Name.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) { chosen = f; break; }
                    if (chosen != null) break;
                }
                if (chosen == null)
                    foreach (FileEntry f in all)
                        if (f.Name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) &&
                            f.Name.IndexOf("mmproj", StringComparison.OrdinalIgnoreCase) < 0) { chosen = f; break; }
                if (chosen != null) pick.Add(chosen);
                if (chosen != null)
                    foreach (FileEntry f in PlanSet(all, chosen)) pick.Add(f);
            }
            string dir = Path.Combine(modelsDir, src == Source.Civitai ? "civitai-" + SafeName(j.Repo) : FolderFor(j.Repo));
            foreach (FileEntry f in pick)
            {
                JobFile jf = new JobFile();
                jf.Repo = j.Repo; jf.Path = f.Name;
                jf.SaveAs = string.IsNullOrEmpty(f.SaveAs) ? f.Name : f.SaveAs;
                jf.Dir = dir;
                jf.Url = f.Url;
                jf.Size = f.Size;
                jf.Hash = f.Hash;
                j.Files.Add(jf);
                j.TotalSize += f.Size;
            }
            return j;
        }

        static string SafeName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "model";
            StringBuilder sb = new StringBuilder();
            foreach (char c in s)
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.') sb.Append(c);
            }
            string r = sb.ToString();
            return r.Length > 40 ? r.Substring(0, 40) : (r.Length == 0 ? "model" : r);
        }

        public static JobItem ResolveRecipeJob(string recipeId, string quant, string modelsDir)
        {
            JobItem empty = new JobItem();
            empty.Kind = "recipe";
            empty.RecipeId = recipeId;
            empty.Quant = quant;
            List<Recipe> rs = LoadRecipes();
            Recipe rec = null;
            foreach (Recipe r in rs) if (r.Id == recipeId) rec = r;
            if (rec == null) return empty;
            int qi = 0;
            if (!string.IsNullOrEmpty(quant)) { int k = rec.QuantLabels.IndexOf(quant); if (k >= 0) qi = k; }
            JobItem j = BuildRecipeJob(rec, qi, modelsDir);
            Dictionary<string, List<FileEntry>> cache = new Dictionary<string, List<FileEntry>>();
            foreach (JobFile f in j.Files)
            {
                // 取回站点给的大小/哈希，队列/下载后校验才有依据
                List<FileEntry> lst;
                if (!cache.TryGetValue(f.Repo, out lst))
                {
                    try { lst = FetchFiles(Source.HF, DefaultMirror, f.Repo); }
                    catch { lst = new List<FileEntry>(); }
                    cache[f.Repo] = lst;
                }
                bool got = false;
                foreach (FileEntry e in lst)
                {
                    if (string.Equals(e.Name, f.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        f.Size = e.Size; f.Hash = e.Hash; got = true; break;
                    }
                }
                if (f.Size > 0) j.TotalSize += f.Size;
                else if (!got)
                {
                    long len = HeadLength(Source.HF, DefaultMirror, f.Repo, f.Path);
                    if (len > 0) j.TotalSize += len;
                }
            }
            return j;
        }

        public class RecipeIssue
        {
            public string Id;
            public string Title;
            public string Message;
        }

        // 递归枚举可能撞上无权限目录 / 坏 junction / 超长路径，出错就不要让它把程序带崩
        static string[] SafeFiles(string dir, string pattern)
        {
            try { return Directory.GetFiles(dir, pattern, SearchOption.AllDirectories); }
            catch { return new string[0]; }
        }

        public static List<RecipeIssue> RecipeIssuesDetailed(string modelsDir)
        {
            List<RecipeIssue> issues = new List<RecipeIssue>();
            List<Recipe> recipes = LoadRecipes();
            string img = Path.Combine(modelsDir, "image");
            if (!Directory.Exists(img)) return issues;

            List<string> common = new List<string>();
            List<string> commonPartial = new List<string>();
            string cdir = CommonFolder(modelsDir);
            if (Directory.Exists(cdir))
            {
                foreach (string f in SafeFiles(cdir, "*"))
                {
                    string n = Path.GetFileName(f);
                    if (n.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) { commonPartial.Add(n.Substring(0, n.Length - 5)); continue; }
                    if (n.EndsWith(".aria2", StringComparison.OrdinalIgnoreCase)) continue;
                    common.Add(n);
                }
            }

            string[] subdirs;
            try { subdirs = Directory.GetDirectories(img); }
            catch { subdirs = new string[0]; }
            foreach (string d in subdirs)
            {
                string id = Path.GetFileName(d);
                if (string.Equals(id, "common", StringComparison.OrdinalIgnoreCase)) continue;
                Recipe rec = null;
                foreach (Recipe r in recipes) if (r.Id == id) rec = r;
                if (rec == null)
                {
                    RecipeIssue ni = new RecipeIssue();
                    ni.Id = id; ni.Title = id;
                    ni.Message = id + "：不在配方表里，LocModel 可能无法使用";
                    issues.Add(ni);
                    continue;
                }

                List<string> have = new List<string>();
                List<string> partial = new List<string>();
                foreach (string f in SafeFiles(d, "*"))
                {
                    string n = Path.GetFileName(f);
                    if (n.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) { partial.Add(n.Substring(0, n.Length - 5)); continue; }
                    if (n.EndsWith(".aria2", StringComparison.OrdinalIgnoreCase)) continue;
                    have.Add(n);
                }

                bool hasMain = false;
                int quantCount = 0;
                List<string> partialQuant = new List<string>();
                for (int qi2 = 0; qi2 < rec.Quants.Count; qi2++)
                {
                    List<Component> g = rec.Quants[qi2];
                    int present = 0;
                    List<string> mg = new List<string>();
                    foreach (Component c in g) { if (have.Contains(c.SaveAs)) present++; else mg.Add(c.SaveAs); }
                    if (g.Count > 0 && present == g.Count) { hasMain = true; quantCount++; }
                    else if (present > 0) partialQuant.Add(rec.QuantLabels[qi2] + " 缺 " + string.Join("、", mg.ToArray()));
                }

                List<string> miss = new List<string>();
                List<string> misplaced = new List<string>();
                foreach (Component c in rec.Fixed)
                {
                    if (common.Contains(c.SaveAs)) continue;
                    if (have.Contains(c.SaveAs)) misplaced.Add(c.SaveAs);
                    else if (commonPartial.Contains(c.SaveAs)) miss.Add(c.SaveAs + "（只有 .part）");
                    else miss.Add(c.SaveAs);
                }

                string msg = null;
                if (!hasMain)
                {
                    if (partial.Count > 0) msg = rec.Title + "：主体没下完（" + partial[0] + " 只有 .part）";
                    else if (partialQuant.Count > 0) msg = rec.Title + "：主体不全（" + partialQuant[0] + "）";
                    else msg = rec.Title + "：缺主体模型";
                }
                else if (miss.Count > 0) msg = rec.Title + "：缺 " + string.Join("、", miss.ToArray()) + "（应放在 image\\common）";
                else if (misplaced.Count > 0) msg = rec.Title + "：配套文件位置不对（" + string.Join("、", misplaced.ToArray()) + " 应移到 image\\common）";
                else if (quantCount > 1) msg = rec.Title + "：有 " + quantCount + " 个量化，占磁盘，可只留一个";

                if (msg != null)
                {
                    RecipeIssue ri = new RecipeIssue();
                    ri.Id = rec.Id; ri.Title = rec.Title; ri.Message = msg;
                    issues.Add(ri);
                }
            }
            return issues;
        }

        public static List<string> RecipeIssues(string modelsDir)
        {
            List<string> list = new List<string>();
            foreach (RecipeIssue i in RecipeIssuesDetailed(modelsDir)) list.Add(i.Message);
            return list;
        }

        public static int FixMisplaced(string modelsDir, Action<string> log)
        {
            int moved = 0;
            try
            {
                string img = Path.Combine(modelsDir, "image");
                if (!Directory.Exists(img)) return 0;
                string cdir = CommonFolder(modelsDir);
                Directory.CreateDirectory(cdir);
                foreach (Recipe rec in LoadRecipes())
                {
                    string rdir = RecipeFolder(modelsDir, rec.Id);
                    if (!Directory.Exists(rdir)) continue;
                    foreach (Component c in rec.Fixed)
                    {
                        string src = Path.Combine(rdir, c.SaveAs);
                        string dst = Path.Combine(cdir, c.SaveAs);
                        if (!File.Exists(src) || File.Exists(dst)) continue;
                        try
                        {
                            File.Move(src, dst);
                            try { if (File.Exists(dst + ".part")) File.Delete(dst + ".part"); } catch { }
                            try { if (File.Exists(src + ".part")) File.Delete(src + ".part"); } catch { }
                            try { if (File.Exists(src + ".aria2")) File.Delete(src + ".aria2"); } catch { }
                            moved++;
                            if (log != null) log("=== 已移动：" + c.SaveAs + "  ->  image\\common");
                        }
                        catch (Exception ex) { if (log != null) log("=== 移动失败：" + c.SaveAs + "  " + ex.Message); }
                    }
                }
            }
            catch { }
            return moved;
        }

        public static string RecipeNameForComponent(string repo, string file)
        {
            foreach (Recipe r in LoadRecipes())
            {
                List<Component> all = new List<Component>();
                foreach (List<Component> g in r.Quants) all.AddRange(g);
                all.AddRange(r.Fixed);
                foreach (Component c in all)
                    if (string.Equals(c.Repo, repo, StringComparison.OrdinalIgnoreCase) &&
                        (c.Path == file || c.SaveAs == file))
                        return r.Title;
            }
            return null;
        }

        public static int CleanStaleParts(string modelsDir, Action<string> log)
        {
            int n = 0;
            try
            {
                if (!Directory.Exists(modelsDir)) return 0;
                foreach (string p in Directory.GetFiles(modelsDir, "*.part", SearchOption.AllDirectories))
                {
                    string full = p.Substring(0, p.Length - 5);
                    if (!File.Exists(full)) continue;
                    try
                    {
                        File.Delete(p);
                        n++;
                        if (log != null) log("=== 清理残留 .part：" + p);
                    }
                    catch { }
                }
            }
            catch { }
            return n;
        }

        public static int SelfTest(Action<string> log)
        {
            int fail = 0;
            Action<bool, string> check = delegate (bool ok, string name)
            {
                if (ok) { if (log != null) log("PASS  " + name); }
                else { fail++; if (log != null) log("FAIL  " + name); }
            };

            // 1) 分片 + mmproj 自动成套
            try
            {
                List<FileEntry> all = new List<FileEntry>();
                all.Add(new FileEntry { Name = "m-Q4_K_M-00001-of-00002.gguf" });
                all.Add(new FileEntry { Name = "m-Q4_K_M-00002-of-00002.gguf" });
                all.Add(new FileEntry { Name = "m.mmproj-Q8_0.gguf" });
                all.Add(new FileEntry { Name = "other.gguf" });
                List<FileEntry> ex = PlanSet(all, all[0]);
                check(ex.Count == 2, "PlanSet 分片+mmproj（期望2，实际" + ex.Count + "）");
            }
            catch (Exception e) { fail++; if (log != null) log("FAIL  PlanSet 抛异常: " + e.Message); }

            // 2) 目录/完整度判定
            string tmp = null;
            string[] candidates =
            {
                Path.Combine(Path.GetTempPath(), "locmodel_selftest"),
                Path.Combine(DataDir() ?? Path.GetTempPath(), "selftest_tmp"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest_tmp")
            };
            foreach (string c in candidates)
            {
                try { Directory.CreateDirectory(c); tmp = c; break; }
                catch { }
            }
            if (tmp == null)
            {
                if (log != null) log("FAIL  找不到可写的临时目录，跳过文件布局测试");
                return fail + 1;
            }
            string oldHome = Environment.GetEnvironmentVariable("LOCMODEL_DL_HOME");
            try
            {
                if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
                Directory.CreateDirectory(Path.Combine(tmp, "image", "z-image-turbo"));
                Directory.CreateDirectory(Path.Combine(tmp, "image", "common"));
                Environment.SetEnvironmentVariable("LOCMODEL_DL_HOME", tmp);
                SaveRecipes(BuiltinRecipes());

                string rdir = RecipeFolder(tmp, "z-image-turbo");
                string cdir = CommonFolder(tmp);
                string main = "z_image_turbo-Q8_0.gguf";
                string te = "Qwen3-4B-Instruct-2507-Q4_K_M.gguf";
                string vae = "ae.safetensors";

                // 2a 只有主体 → 报缺配件
                File.WriteAllText(Path.Combine(rdir, main), "x");
                List<RecipeIssue> i1 = RecipeIssuesDetailed(tmp);
                bool found1 = false;
                foreach (RecipeIssue i in i1) if (i.Id == "z-image-turbo" && i.Message.IndexOf("缺", StringComparison.Ordinal) >= 0) found1 = true;
                check(found1, "只下主体时报缺配件");

                // 2b 配件放错在配方目录 → 报位置不对
                File.WriteAllText(Path.Combine(rdir, te), "x");
                File.WriteAllText(Path.Combine(rdir, vae), "x");
                bool found2 = false;
                foreach (RecipeIssue i in RecipeIssuesDetailed(tmp)) if (i.Message.IndexOf("位置不对", StringComparison.Ordinal) >= 0) found2 = true;
                check(found2, "配件放错目录时报位置不对");

                // 2c 移到 common → 不再报问题
                File.Move(Path.Combine(rdir, te), Path.Combine(cdir, te));
                File.Move(Path.Combine(rdir, vae), Path.Combine(cdir, vae));
                bool clean = true;
                foreach (RecipeIssue i in RecipeIssuesDetailed(tmp)) if (i.Id == "z-image-turbo") clean = false;
                check(clean, "配件在 common 时判定为完整");

                // 2d 只有 .part → 报未下完
                Directory.CreateDirectory(Path.Combine(tmp, "image", "flux2-klein-4b"));
                File.WriteAllText(Path.Combine(tmp, "image", "flux2-klein-4b", "flux-2-klein-4b-Q8_0.gguf.part"), "x");
                bool found4 = false;
                foreach (RecipeIssue i in RecipeIssuesDetailed(tmp)) if (i.Message.IndexOf("没下完", StringComparison.Ordinal) >= 0) found4 = true;
                check(found4, "只有 .part 时报主体没下完");

                // 2e FixMisplaced 能搬运
                File.Delete(Path.Combine(cdir, te));
                File.WriteAllText(Path.Combine(rdir, te), "x");
                int moved = FixMisplaced(tmp, null);
                check(moved >= 1 && File.Exists(Path.Combine(cdir, te)), "FixMisplaced 搬到 common");

                // 2f 完整文件旁边的残留 .part 会被清理
                File.WriteAllText(Path.Combine(rdir, main + ".part"), "y");
                int cleaned = CleanStaleParts(tmp, null);
                check(cleaned >= 1 && !File.Exists(Path.Combine(rdir, main + ".part")) && File.Exists(Path.Combine(rdir, main)),
                    "清理残留 .part");
                // 3) SameJob：不同文件不算重复，同文件算重复，落盘读回仍能判重
                JobItem ja = new JobItem(); ja.Src = "hf"; ja.Repo = "o/r";
                JobFile fa = new JobFile(); fa.Repo = "o/r"; fa.Path = "a.gguf"; fa.SaveAs = "a.gguf"; ja.Files.Add(fa);
                JobItem jb = new JobItem(); jb.Src = "hf"; jb.Repo = "o/r";
                JobFile fb = new JobFile(); fb.Repo = "o/r"; fb.Path = "b.gguf"; fb.SaveAs = "b.gguf"; jb.Files.Add(fb);
                check(!SameJob(ja, jb), "同仓库不同文件不算重复");
                JobItem jc = new JobItem(); jc.Src = "hf"; jc.Repo = "o/r";
                JobFile fc = new JobFile(); fc.Repo = "o/r"; fc.Path = "a.gguf"; fc.SaveAs = "a.gguf"; jc.Files.Add(fc);
                check(SameJob(ja, jc), "同仓库同文件判为重复");
                SaveQueue(new List<JobItem> { ja });
                List<JobItem> rq = LoadQueue();
                string diff = "";
                if (rq.Count == 1)
                {
                    JobItem x = rq[0];
                    if (x.Src != jc.Src) diff += " src=" + x.Src;
                    if (x.Kind != jc.Kind) diff += " kind=" + x.Kind;
                    if (x.Repo != jc.Repo) diff += " repo=" + x.Repo;
                    if (x.RecipeId != jc.RecipeId) diff += " rid=" + x.RecipeId;
                    if (x.Quant != jc.Quant) diff += " q=" + x.Quant;
                    if (x.Files.Count != 1) diff += " files=" + x.Files.Count;
                    else
                    {
                        if (x.Files[0].Repo != "o/r") diff += " f.repo=" + x.Files[0].Repo;
                        if (x.Files[0].Path != "a.gguf") diff += " f.path=" + x.Files[0].Path;
                        if (x.Files[0].SaveAs != "a.gguf") diff += " f.save=" + x.Files[0].SaveAs;
                    }
                }
                check(rq.Count == 1 && SameJob(rq[0], jc),
                    "队列落盘读回后仍能判重" + (rq.Count == 1 ? "[" + diff + "]" : "[count=" + rq.Count + "]"));

                // 4) sha256 与校验判定
                string tf = Path.Combine(tmp, "abc_test.bin");
                File.WriteAllText(tf, "abc", new UTF8Encoding(false));
                const string abcSha = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
                string h = Sha256File(tf, null);
                check(string.Equals(h, abcSha, StringComparison.OrdinalIgnoreCase), "sha256 计算（abc）");

                string e1 = VerifyFile(tf, 3, abcSha, null, out h);
                check(e1 == null, "校验：大小+哈希一致时通过");
                string e2 = VerifyFile(tf, 999, null, null, out h);
                check(e2 != null, "校验：大小不符时报错");
                string e3 = VerifyFile(tf, 3, new string('0', 64), null, out h);
                check(e3 != null && e3.IndexOf("sha256", StringComparison.Ordinal) >= 0, "校验：哈希不符时报错");
                check(NormalizeHash("XYZ") == null && NormalizeHash("SHA256:" + abcSha) == abcSha, "哈希格式归一化");
            }
            catch (Exception e) { fail++; if (log != null) log("FAIL  文件布局测试异常: " + e.Message); }
            finally
            {
                try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
                Environment.SetEnvironmentVariable("LOCMODEL_DL_HOME", oldHome);
            }

            if (log != null) log("---- 自检结束：失败 " + fail + " 项 ----");
            return fail;
        }

        public static Icon BrandIcon()
        {
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { return null; }
        }

        public static void ApplyIcon(Form f)
        {
            try
            {
                Icon ic = BrandIcon();
                if (ic != null) f.Icon = ic;
            }
            catch { }
        }

        public static string FindAria()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            // 自动安装位置（数据目录下的 aria2\）
            try
            {
                string dd = DataDir();
                if (!string.IsNullOrEmpty(dd))
                {
                    string installed = Path.Combine(dd, "aria2", "aria2c.exe");
                    if (File.Exists(installed)) return installed;
                }
            }
            catch { }

            string[] local =
            {
                Path.Combine(baseDir, "aria2c.exe"),
                Path.Combine(baseDir, "aria2", "aria2c.exe"),
                Path.Combine(baseDir, "tools", "aria2c.exe"),
                Path.Combine(baseDir, "tools", "aria2", "aria2c.exe")
            };
            foreach (string p in local) if (File.Exists(p)) return p;

            try
            {
                string tools = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "tools");
                if (Directory.Exists(tools))
                {
                    string[] found = Directory.GetFiles(tools, "aria2c.exe", SearchOption.AllDirectories);
                    if (found.Length > 0) return found[0];
                }
            }
            catch { }

            foreach (string d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                if (d.Trim().Length == 0) continue;
                string p = Path.Combine(d.Trim(), "aria2c.exe");
                if (File.Exists(p)) return p;
            }

            string[] fixedPaths =
            {
                @"C:\Program Files\aria2\aria2c.exe",
                @"C:\Program Files (x86)\aria2\aria2c.exe",
                @"D:\ProgramData\tools\aria2\aria2c.exe"
            };
            foreach (string p in fixedPaths) if (File.Exists(p)) return p;
            return null;
        }

        // ---------- 自动安装 aria2 ----------

        public const string Aria2Version = "1.37.0";
        public const string Aria2Url = "https://github.com/aria2/aria2/releases/download/release-1.37.0/aria2-1.37.0-win-64bit-build1.zip";
        public const string Aria2Sha256 = "BE2099C214F63A3CB4954B09A0BECD6E2E34660B886D4C898D260FEBFE9D70C2";

        public static string AriaInstallDir()
        {
            try
            {
                string dir = DataDir();
                return dir == null ? null : Path.Combine(dir, "aria2");
            }
            catch { return null; }
        }

        // 从 aria2 官方发布包下载并校验后安装到数据目录；返回 aria2c.exe 路径
        public static string InstallAria2(Action<string> log)
        {
            string dir = AriaInstallDir();
            if (dir == null) throw new Exception("找不到可写的数据目录，无法自动安装。");
            Directory.CreateDirectory(dir);
            string exe = Path.Combine(dir, "aria2c.exe");

            // 有些机器的 TEMP 根目录不允许直接建文件，改用自己的子目录（失败就退回数据目录）
            string tmpDir = null;
            try
            {
                tmpDir = Path.Combine(Path.GetTempPath(), "locmodel_aria_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tmpDir);
            }
            catch { tmpDir = null; }
            if (tmpDir == null || !Directory.Exists(tmpDir))
            {
                tmpDir = Path.Combine(dir, "tmp_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tmpDir);
            }
            string tmpZip = Path.Combine(tmpDir, "aria2.zip");
            string tmpExe = Path.Combine(tmpDir, "aria2c.exe");
            try
            {
                if (log != null) log("正在下载 aria2 " + Aria2Version + "（约 2.4 MB）…");
                try
                {
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(Aria2Url);
                    req.UserAgent = "Mozilla/5.0 LocModelDownloader";
                    req.Timeout = 120000;
                    req.ReadWriteTimeout = 120000;
                    req.AllowAutoRedirect = true;
                    if (!string.IsNullOrEmpty(proxy)) req.Proxy = new WebProxy(proxy);
                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    using (Stream rs = resp.GetResponseStream())
                    using (FileStream fs = new FileStream(tmpZip, FileMode.Create, FileAccess.Write))
                        rs.CopyTo(fs);
                }
                catch (WebException we)
                {
                    string code = "";
                    try
                    {
                        HttpWebResponse hr = we.Response as HttpWebResponse;
                        if (hr != null) code = "（HTTP " + (int)hr.StatusCode + "）";
                    }
                    catch { }
                    string detail = we.Message;
                    if (we.InnerException != null) detail += " / " + we.InnerException.Message;
                    throw new Exception("下载 aria2 失败" + code + "：" + detail +
                                        "\r\n可手动下载：" + Aria2Url);
                }

                if (log != null) log("下载完成，正在解压并校验…");
                bool found = false;
                using (ZipArchive za = ZipFile.OpenRead(tmpZip))
                {
                    foreach (ZipArchiveEntry e in za.Entries)
                    {
                        if (!string.Equals(e.Name, "aria2c.exe", StringComparison.OrdinalIgnoreCase)) continue;
                        e.ExtractToFile(tmpExe, true);
                        found = true;
                        break;
                    }
                }
                if (!found) throw new Exception("下载的压缩包里没有 aria2c.exe。");

                string h = Sha256File(tmpExe, null);
                if (!string.Equals(h, Aria2Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("aria2c.exe 校验失败（哈希不符），已中止安装。\r\n实际：" + h);

                try { if (File.Exists(exe)) File.Delete(exe); } catch { }
                File.Copy(tmpExe, exe, true);
                if (log != null) log("已安装：" + exe);
                Log("自动安装 aria2 完成: " + exe);
                return exe;
            }
            finally
            {
                try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // ---------- 列表 ----------

        static string ReadBody(HttpWebRequest req, Source src)
        {
            try
            {
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch (WebException we)
            {
                HttpWebResponse hr = we.Response as HttpWebResponse;
                int code = hr == null ? 0 : (int)hr.StatusCode;
                if (code == 401 || code == 403)
                {
                    string who = src == Source.HF ? "HuggingFace" : (src == Source.MS ? "魔塔" : "C站");
                    string extra = src == Source.HF
                        ? "：仓库可能不存在、名字拼错，或是私有 / 需同意协议的仓库（点「Token 设置」填令牌再试）"
                        : "：仓库可能不存在、名字拼错，或需要登录令牌";
                    throw new AuthNeeded(src, who + " 拒绝了请求（" + code + "）" + extra);
                }
                throw;
            }
        }

        public static List<FileEntry> FetchFiles(Source src, string mirror, string repo)
        {
            List<FileEntry> list;
            if (src == Source.MS) list = FetchModelScope(repo);
            else if (src == Source.Civitai) list = FetchCivitai(repo);
            else list = FetchHf(mirror, repo);
            list.Sort(delegate (FileEntry a, FileEntry b)
            {
                int ga = a.Name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                int gb = b.Name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                if (ga != gb) return ga - gb;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }

        static List<FileEntry> FetchHf(string mirror, string repo)
        {
            string url = mirror.TrimEnd('/') + "/api/models/" + NormRepo(repo) + "?blobs=true";
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "Mozilla/5.0 LocModelDownloader";
            req.Timeout = 30000;
            req.ReadWriteTimeout = 30000;
            if (!string.IsNullOrEmpty(Secrets.Hf)) req.Headers["Authorization"] = "Bearer " + Secrets.Hf;
            string body = ReadBody(req, Source.HF);

            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = 64 * 1024 * 1024;
            Dictionary<string, object> root = null;
            try { root = js.DeserializeObject(body) as Dictionary<string, object>; }
            catch { }
            if (root == null)
            {
                string head = body == null ? "" : body.TrimStart();
                if (head.StartsWith("<"))
                    throw new Exception("镜像返回的是网页而不是接口数据（可能被代理/镜像拦截了）：\r\n" +
                                        body.Substring(0, Math.Min(120, body.Length)));
                throw new Exception("仓库不存在或无法访问：" + NormRepo(repo) +
                                    "\r\n（请确认是「owner/仓库名」，例如 Qwen/Qwen2.5-7B-Instruct-GGUF）");
            }
            if (root.ContainsKey("error"))
                throw new Exception("HuggingFace 返回错误：" + Convert.ToString(root["error"]) + "\r\n（仓库：" + NormRepo(repo) + "）");
            List<FileEntry> list = new List<FileEntry>();
            object sib;
            if (!root.ContainsKey("siblings") || root["siblings"] == null) return list;
            sib = root["siblings"];
            System.Collections.IEnumerable seq = sib as System.Collections.IEnumerable;
            if (seq == null) return list;
            foreach (object o in seq)
            {
                Dictionary<string, object> d = o as Dictionary<string, object>;
                if (d == null) continue;
                FileEntry fe = new FileEntry();
                fe.Repo = NormRepo(repo);
                object v;
                if (d.TryGetValue("rfilename", out v) && v != null) fe.Name = v.ToString();
                fe.SaveAs = fe.Name;
                if (d.TryGetValue("size", out v) && v != null)
                {
                    long sz;
                    if (long.TryParse(v.ToString(), out sz)) fe.Size = sz;
                }
                if (d.TryGetValue("lfs", out v) && v != null)
                {
                    Dictionary<string, object> lf = v as Dictionary<string, object>;
                    object so;
                    if (lf != null && lf.TryGetValue("sha256", out so) && so != null)
                        fe.Hash = NormalizeHash(Convert.ToString(so));
                }
                if (!string.IsNullOrEmpty(fe.Name)) list.Add(fe);
            }
            return list;
        }

        static List<FileEntry> FetchModelScope(string repo)
        {
            string r = NormRepo(repo);
            string url = MsBase + "/api/v1/models/" + r + "/repo/files?Revision=master&Recursive=true";
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "Mozilla/5.0 LocModelDownloader";
            req.Timeout = 30000;
            req.ReadWriteTimeout = 30000;
            if (!string.IsNullOrEmpty(Secrets.Ms)) req.Headers["Authorization"] = "Bearer " + Secrets.Ms;
            string body = ReadBody(req, Source.MS);

            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = 64 * 1024 * 1024;
            Dictionary<string, object> root = null;
            try { root = js.DeserializeObject(body) as Dictionary<string, object>; }
            catch { }
            if (root == null)
                throw new Exception("魔塔仓库不存在或无法访问：" + r + "\r\n（链接只保留 owner/仓库名即可，例如 Qwen/Qwen2.5-7B-Instruct-GGUF）");
            List<FileEntry> list = new List<FileEntry>();
            object data;
            if (!root.ContainsKey("Data") || root["Data"] == null)
            {
                string msg = root.ContainsKey("Message") ? Convert.ToString(root["Message"]) : "";
                throw new Exception("魔塔返回错误：" + (msg.Length > 0 ? msg : "没有文件列表") + "\r\n（仓库：" + r + "）");
            }
            data = root["Data"];
            Dictionary<string, object> dd = data as Dictionary<string, object>;
            if (dd == null || !dd.ContainsKey("Files") || dd["Files"] == null) return list;
            System.Collections.IEnumerable seq = dd["Files"] as System.Collections.IEnumerable;
            if (seq == null) return list;
            foreach (object o in seq)
            {
                Dictionary<string, object> d = o as Dictionary<string, object>;
                if (d == null) continue;
                object v;
                string type = d.ContainsKey("Type") ? Convert.ToString(d["Type"]) : "";
                if (!string.Equals(type, "blob", StringComparison.OrdinalIgnoreCase)) continue;

                FileEntry fe = new FileEntry();
                fe.Repo = r;
                string path = d.ContainsKey("Path") ? Convert.ToString(d["Path"]) : "";
                if (string.IsNullOrEmpty(path) && d.ContainsKey("Name")) path = Convert.ToString(d["Name"]);
                fe.Name = path;
                fe.SaveAs = path;
                if (d.TryGetValue("Size", out v) && v != null)
                {
                    long sz;
                    if (long.TryParse(v.ToString(), out sz)) fe.Size = sz;
                }
                if (d.TryGetValue("Sha256", out v) && v != null)
                    fe.Hash = NormalizeHash(Convert.ToString(v));
                if (!string.IsNullOrEmpty(fe.Name)) list.Add(fe);
            }
            return list;
        }

        // ---------- 下载 ----------

        public static string ResolveCivitaiVersion(string input)
        {
            string s = (input ?? "").Trim();
            Match m = Regex.Match(s, @"modelVersionId=(\d+)", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value;
            m = Regex.Match(s, @"/model-version[s]?/(\d+)", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value;
            Match mm = Regex.Match(s, @"/models?/(\d+)", RegexOptions.IgnoreCase);
            if (mm.Success && !Regex.IsMatch(s.Trim('/'), @"^\d+$"))
                return LatestCivitaiVersion(mm.Groups[1].Value);
            m = Regex.Match(s, @"(\d+)\s*$");
            if (m.Success) return m.Groups[1].Value;
            throw new Exception("Civitai 需要「模型版本 ID」、「模型主页链接（civitai.com/models/数字/...）」或带 modelVersionId 的链接");
        }

        static string LatestCivitaiVersion(string modelId)
        {
            string url = "https://civitai.com/api/v1/models/" + modelId;
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "Mozilla/5.0 LocModelDownloader";
            req.Timeout = 60000;
            req.ReadWriteTimeout = 60000;
            if (!string.IsNullOrEmpty(Secrets.Civitai))
                req.Headers["Authorization"] = "Bearer " + Secrets.Civitai;
            string body = ReadBody(req, Source.Civitai);

            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = 32 * 1024 * 1024;
            Dictionary<string, object> root = null;
            try { root = js.DeserializeObject(body) as Dictionary<string, object>; }
            catch { }
            object vobj = null;
            if (root != null)
            {
                if (root.ContainsKey("modelVersions")) vobj = root["modelVersions"];
                else if (root.ContainsKey("versions")) vobj = root["versions"];
            }
            if (vobj == null)
                throw new Exception("C站模型 " + modelId + " 读取失败（可能已删除或无权限）");

            System.Collections.IEnumerable seq = vobj as System.Collections.IEnumerable;
            if (seq == null) throw new Exception("C站模型 " + modelId + " 没有版本信息");
            // 接口按发布时间倒序返回，取第一个公开版本（跳过会员专享）
            string firstId = null, anyId = null;
            foreach (object o in seq)
            {
                Dictionary<string, object> d = o as Dictionary<string, object>;
                if (d == null || !d.ContainsKey("id") || d["id"] == null) continue;
                string idStr = Convert.ToString(d["id"]);
                if (anyId == null) anyId = idStr;
                string avail = d.ContainsKey("availability") ? Convert.ToString(d["availability"]) : "";
                if (!string.Equals(avail, "EarlyAccess", StringComparison.OrdinalIgnoreCase))
                {
                    firstId = idStr;
                    break;
                }
            }
            if (firstId != null) return firstId;
            if (anyId != null) return anyId;
            throw new Exception("C站模型 " + modelId + " 没有可用版本");
        }

        static List<FileEntry> FetchCivitai(string input)
        {
            string id = ResolveCivitaiVersion(input);

            string url = "https://civitai.com/api/v1/model-versions/" + id;
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "Mozilla/5.0 LocModelDownloader";
            req.Timeout = 60000;
            req.ReadWriteTimeout = 60000;
            if (!string.IsNullOrEmpty(Secrets.Civitai))
                req.Headers["Authorization"] = "Bearer " + Secrets.Civitai;
            string body = ReadBody(req, Source.Civitai);

            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = 32 * 1024 * 1024;
            Dictionary<string, object> root = null;
            try { root = js.DeserializeObject(body) as Dictionary<string, object>; }
            catch { }
            if (root == null)
                throw new Exception("C站返回的内容不是预期格式（可能被 Cloudflare 拦截或需要代理）：\r\n" +
                                    (body == null ? "" : body.Substring(0, Math.Min(120, body.Length))));
            List<FileEntry> list = new List<FileEntry>();
            object filesObj;
            if (!root.ContainsKey("files") || root["files"] == null) return list;
            filesObj = root["files"];
            System.Collections.IEnumerable seq = filesObj as System.Collections.IEnumerable;
            if (seq == null) return list;
            foreach (object o in seq)
            {
                Dictionary<string, object> d = o as Dictionary<string, object>;
                if (d == null) continue;
                FileEntry fe = new FileEntry();
                fe.Repo = "civitai-" + id;
                object v;
                if (d.TryGetValue("name", out v) && v != null) fe.Name = v.ToString();
                if (d.TryGetValue("sizeKB", out v) && v != null)
                {
                    double kb;
                    if (double.TryParse(v.ToString(), out kb)) fe.Size = (long)(kb * 1024);
                }
                if (d.TryGetValue("hashes", out v) && v != null)
                {
                    Dictionary<string, object> hh = v as Dictionary<string, object>;
                    object sh;
                    if (hh != null && hh.TryGetValue("SHA256", out sh) && sh != null)
                        fe.Hash = NormalizeHash(Convert.ToString(sh));
                }
                if (d.TryGetValue("downloadUrl", out v) && v != null) fe.Url = v.ToString();
                if (!string.IsNullOrEmpty(fe.Name)) list.Add(fe);
            }
            return list;
        }

        public static string UrlFor(Source src, string mirror, string repo, string subPath)
        {
            StringBuilder u = new StringBuilder();
            u.Append(src == Source.MS ? MsBase + "/models/" : mirror.TrimEnd('/') + "/");
            foreach (string p in NormRepo(repo).Split('/')) u.Append(Uri.EscapeDataString(p)).Append('/');
            u.Append(src == Source.MS ? "resolve/master/" : "resolve/main/");
            foreach (string p in subPath.Split('/')) u.Append(Uri.EscapeDataString(p)).Append('/');
            u.Length -= 1;
            return u.ToString();
        }

        public static int HeadStatus(Source src, string mirror, string repo, string subPath)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(UrlFor(src, mirror, repo, subPath));
                req.Method = "HEAD";
                req.UserAgent = "Mozilla/5.0 LocModelDownloader";
                req.Timeout = 20000;
                req.AllowAutoRedirect = true;
                string tok = src == Source.MS ? SanitizeToken(Secrets.Ms) : SanitizeToken(Secrets.Hf);
                if (!string.IsNullOrEmpty(tok)) req.Headers["Authorization"] = "Bearer " + tok;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    return (int)resp.StatusCode;
            }
            catch (WebException we)
            {
                using (HttpWebResponse hr = we.Response as HttpWebResponse)
                    return hr == null ? 0 : (int)hr.StatusCode;
            }
            catch { return 0; }
        }

        public static int HeadUrl(string url)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "HEAD";
                req.UserAgent = "Mozilla/5.0 LocModelDownloader";
                req.Timeout = 20000;
                req.AllowAutoRedirect = true;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    return (int)resp.StatusCode;
            }
            catch (WebException we)
            {
                using (HttpWebResponse hr = we.Response as HttpWebResponse)
                    return hr == null ? 0 : (int)hr.StatusCode;
            }
            catch { return 0; }
        }

        public static long HeadLength(Source src, string mirror, string repo, string subPath)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(UrlFor(src, mirror, repo, subPath));
                req.Method = "HEAD";
                req.UserAgent = "Mozilla/5.0 LocModelDownloader";
                req.Timeout = 20000;
                req.AllowAutoRedirect = true;
                string tok = src == Source.MS ? SanitizeToken(Secrets.Ms) :
                             (src == Source.Civitai ? SanitizeToken(Secrets.Civitai) : SanitizeToken(Secrets.Hf));
                if (!string.IsNullOrEmpty(tok)) req.Headers["Authorization"] = "Bearer " + tok;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    string cl = resp.Headers["Content-Length"];
                    long v;
                    if (long.TryParse(cl, out v)) return v;
                }
            }
            catch { }
            return -1;
        }

        public static bool HeadOk(Source src, string mirror, string repo, string subPath)
        {
            int s = HeadStatus(src, mirror, repo, subPath);
            return s >= 200 && s < 400;
        }

        public static int RunAriaTo(Source src, string mirror, string repo, string subPath, string saveAs, string targetDir,
            Action<string> log, Func<bool> cancelled, out string error, string urlOverride = null,
            long expectSize = -1, string expectHash = null)
        {
            error = null;
            string aria = FindAria();
            if (aria == null)
            {
                error = "找不到 aria2c.exe。请把 aria2c.exe（或整个 aria2 文件夹）放到本工具同一目录，或把它加入系统 PATH。";
                return -1;
            }

            Directory.CreateDirectory(targetDir);
            string outPath = Path.Combine(targetDir, saveAs);
            string outParent = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(outParent)) Directory.CreateDirectory(outParent);
            bool resumable = File.Exists(outPath + ".aria2");

            // LocModel 的 .part 没有 aria2 控制文件，接不上续传，这里直接清掉
            try
            {
                if (File.Exists(outPath + ".part"))
                {
                    File.Delete(outPath + ".part");
                    if (log != null) log("=== 已清理 LocModel 残留的 .part：" + saveAs);
                }
            }
            catch { }

            if (File.Exists(outPath) && !resumable)
            {
                bool ok = true;
                long sz0 = -1;
                try { sz0 = new FileInfo(outPath).Length; } catch { }
                if (sz0 <= 0)
                {
                    ok = false;   // 0 字节肯定不完整
                    if (log != null) log("=== 文件是 0 字节，重新下载：" + saveAs);
                }
                else if (verifyMode != "off" && expectSize > 0)
                {
                    if (sz0 != expectSize)
                    {
                        ok = false;
                        if (log != null) log("=== 大小不符，重新下载：" + saveAs + "（应有 " + Fmt(expectSize) + "，现有 " + Fmt(sz0) + "）");
                    }
                }
                else if (verifyMode != "off" && expectSize <= 0)
                {
                    if (log != null) log("=== 已存在，但站点没给大小，无法校验：" + saveAs);
                }
                string eh0 = NormalizeHash(expectHash);
                if (ok && verifyMode == "hash" && eh0 != null)
                {
                    if (log != null) log("=== 校验已有文件（sha256）：" + saveAs);
                    string h0 = null;
                    try { h0 = Sha256File(outPath, null); }
                    catch (Exception ex) { if (log != null) log("=== 已有文件读不了，直接重下：" + ex.Message); }
                    if (h0 != null && !string.Equals(h0, eh0, StringComparison.OrdinalIgnoreCase))
                    {
                        ok = false;
                        if (log != null) log("=== 已有文件 sha256 不符，重新下载：" + saveAs);
                    }
                    if (h0 == null) ok = false;   // 读不了就重下，别冒险跳过
                }
                if (ok)
                {
                    if (log != null) log("=== 已存在，跳过：" + saveAs);
                    return 0;
                }
                try { File.Delete(outPath); } catch { }
                if (File.Exists(outPath))
                {
                    error = "旧文件删不掉（可能正被 LocModel 或其它程序占用）：" + outPath +
                            "\r\n请先在 LocModel 里暂停/退出，或手动删除后再下载。";
                    return -3;
                }
            }
            if (resumable && log != null) log("=== 断点续传：" + saveAs);

            string url = string.IsNullOrEmpty(urlOverride) ? UrlFor(src, mirror, repo, subPath) : urlOverride;
            // 令牌按来源各用各的：绝不要把 HF 的令牌发给别的站
            string tok = src == Source.MS ? SanitizeToken(Secrets.Ms) : (src == Source.Civitai ? "" : SanitizeToken(Secrets.Hf));
            if (src == Source.Civitai && !string.IsNullOrEmpty(Secrets.Civitai))
            {
                url += (url.IndexOf('?') >= 0 ? "&" : "?") + "token=" + Uri.EscapeDataString(SanitizeToken(Secrets.Civitai));
                tok = "";
            }

            ProcessStartInfo psi = new ProcessStartInfo(aria);
            int cx = connections;
            psi.Arguments = "\"" + url + "\" -x" + cx + " -s" + cx + " -k 1M -d \"" + targetDir + "\" -o \"" + saveAs + "\""
                + " --continue=true --file-allocation=none --max-connection-per-server=" + cx
                + " --min-split-size=1M --retry-wait=3 --max-tries=5 --timeout=30 --auto-save-interval=1"
                + " --summary-interval=2 --console-log-level=warn --check-certificate=true";
            if (!string.IsNullOrEmpty(proxy))
                psi.Arguments += " --all-proxy=\"" + proxy + "\"";
            if (!string.IsNullOrEmpty(tok))
                psi.Arguments += " --header=\"Authorization: Bearer " + tok + "\"";
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;

            using (Process p = new Process())
            {
                p.StartInfo = psi;
                p.OutputDataReceived += delegate (object s, DataReceivedEventArgs e) { if (e.Data != null && log != null) log(e.Data); };
                p.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e) { if (e.Data != null && log != null) log(e.Data); };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                while (!p.HasExited)
                {
                    if (cancelled != null && cancelled())
                    {
                        try { p.Kill(); } catch { }
                        try { p.WaitForExit(3000); } catch { }   // 等 aria2 把 .aria2 断点写完
                        error = "已取消";
                        return -2;
                    }
                    System.Threading.Thread.Sleep(300);
                }
                p.WaitForExit();
                if (p.ExitCode != 0) { error = "aria2 退出码 " + p.ExitCode; return p.ExitCode; }
            }
            try
            {
                if (File.Exists(outPath + ".part")) File.Delete(outPath + ".part");
            }
            catch { }

            // 下载后校验：大小必查（便宜），sha256 按 verify=hash 才查（大文件要几十秒）
            if (verifyMode != "off")
            {
                long actual = -1;
                try { if (File.Exists(outPath)) actual = new FileInfo(outPath).Length; } catch { }
                string eh = NormalizeHash(expectHash);
                string bad = null;
                if (actual < 0)
                    bad = "下载完成后找不到文件（可能被杀毒软件删掉或写到了别处）";
                else if (actual == 0)
                    bad = "下载完成但文件是 0 字节（服务器可能返回了错误页）";
                else if (expectSize > 0 && actual != expectSize)
                    bad = "大小不符：应为 " + Fmt(expectSize) + "，实际 " + Fmt(actual);
                else if (expectSize <= 0 && eh == null && log != null)
                    log("=== 站点没给大小/哈希，只能确认文件存在（" + Fmt(actual) + "）：" + saveAs);

                if (bad == null && verifyMode == "hash" && eh != null)
                {
                    if (log != null) log("=== 校验中（sha256）：" + saveAs);
                    string h = null;
                    bool readErr = false;
                    try { h = Sha256File(outPath, null); }
                    catch (Exception ex) { readErr = true; if (log != null) log("=== 校验时读不了文件：" + ex.Message); }
                    if (readErr || h == null)
                    {
                        // 只是读不了，不代表内容坏：保留文件，别删
                        error = "下载完成，但文件被占用/读不了，没做成哈希校验：" + outPath +
                                "\r\n（多半是 LocModel 或杀毒软件占用，关掉后可用「校验文件」再查一遍）";
                        return -5;
                    }
                    if (!string.Equals(h, eh, StringComparison.OrdinalIgnoreCase))
                        bad = "sha256 不符：应为 " + eh.Substring(0, 12) + "…，实际 " + h.Substring(0, 12) + "…";
                }
                if (bad != null)
                {
                    error = "下载完成但校验失败：" + bad;
                    if (log != null) log("=== 校验失败：" + saveAs + "  " + bad + "（文件已删除，可重新下载）");
                    try { File.Delete(outPath); } catch { }
                    try { if (File.Exists(outPath + ".aria2")) File.Delete(outPath + ".aria2"); } catch { }
                    return -4;
                }
            }
            return 0;
        }

        // ---------- 成套 / 预检 ----------

        public static List<FileEntry> PlanSet(List<FileEntry> all, FileEntry main)
        {
            List<FileEntry> extra = new List<FileEntry>();
            if (all == null || main == null) return extra;
            string name = main.Name;
            if (!name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) return extra;
            if (name.IndexOf("mmproj", StringComparison.OrdinalIgnoreCase) >= 0) return extra;

            Match shard = Regex.Match(Path.GetFileName(name), @"^(.*)-\d{5}-of-\d{5}\.gguf$", RegexOptions.IgnoreCase);
            if (shard.Success)
            {
                string prefix = shard.Groups[1].Value;
                foreach (FileEntry f in all)
                {
                    if (f == main) continue;
                    if (Regex.IsMatch(Path.GetFileName(f.Name), "^" + Regex.Escape(prefix) + @"-\d{5}-of-\d{5}\.gguf$", RegexOptions.IgnoreCase))
                        extra.Add(f);
                }
            }

            List<FileEntry> mm = new List<FileEntry>();
            foreach (FileEntry f in all)
                if (f.Name.IndexOf("mmproj", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    f.Name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
                    mm.Add(f);
            if (mm.Count > 0)
            {
                FileEntry pick = null;
                foreach (FileEntry f in mm) if (f.Name.IndexOf("Q8_0", StringComparison.OrdinalIgnoreCase) >= 0) { pick = f; break; }
                if (pick == null) foreach (FileEntry f in mm) if (f.Name.IndexOf("f16", StringComparison.OrdinalIgnoreCase) >= 0) { pick = f; break; }
                if (pick == null) pick = mm[0];
                extra.Add(pick);
            }
            return extra;
        }

        public static List<string> Preflight(Source src, string mirror, string repo, List<FileEntry> all,
            List<FileEntry> selected, bool checkUrls)
        {
            List<string> warn = new List<string>();
            if (selected == null || selected.Count == 0) { warn.Add("没有勾选任何文件。"); return warn; }

            Dictionary<string, List<string>> groups = new Dictionary<string, List<string>>();
            foreach (FileEntry f in all)
            {
                Match m = Regex.Match(Path.GetFileName(f.Name), @"^(.*)-\d{5}-of-\d{5}\.gguf$", RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                string key = m.Groups[1].Value;
                if (!groups.ContainsKey(key)) groups[key] = new List<string>();
                groups[key].Add(f.Name);
            }
            foreach (KeyValuePair<string, List<string>> kv in groups)
            {
                bool touched = false;
                int picked = 0;
                foreach (FileEntry f in selected) if (kv.Value.Contains(f.Name)) { touched = true; picked++; }
                if (touched && picked < kv.Value.Count)
                    warn.Add("分片不全：" + kv.Key + " 共 " + kv.Value.Count + " 片，只勾了 " + picked + " 片，模型无法加载。");
            }

            List<FileEntry> mm = new List<FileEntry>();
            foreach (FileEntry f in all)
                if (f.Name.IndexOf("mmproj", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    f.Name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
                    mm.Add(f);
            if (mm.Count > 0)
            {
                bool pickedMm = false;
                foreach (FileEntry f in selected) if (mm.Contains(f)) { pickedMm = true; break; }
                if (!pickedMm)
                    warn.Add("缺少看图组件：这个仓库里有 mmproj（" + mm[0].Name + "），没勾它就只能纯文字对话。");
            }

            // 按配方聚合：只有当勾选里缺该配方某量化的组件时才提示，勾全了就不吵
            List<Recipe> allRecipes = LoadRecipes();
            Dictionary<string, List<FileEntry>> perRecipe = new Dictionary<string, List<FileEntry>>();
            foreach (FileEntry f in selected)
            {
                string rec = RecipeNameForComponent(f.Repo, f.Name);
                if (rec == null) continue;
                if (!perRecipe.ContainsKey(rec)) perRecipe[rec] = new List<FileEntry>();
                perRecipe[rec].Add(f);
            }
            foreach (KeyValuePair<string, List<FileEntry>> kv in perRecipe)
            {
                Recipe rec = null;
                foreach (Recipe rr in allRecipes) if (rr.Title == kv.Key) { rec = rr; break; }
                bool complete = false;
                List<string> missing = new List<string>();
                if (rec != null)
                {
                    for (int qi = 0; qi < rec.Quants.Count && !complete; qi++)
                    {
                        List<Component> need = new List<Component>(rec.Quants[qi]);
                        need.AddRange(rec.Fixed);
                        List<string> miss = new List<string>();
                        foreach (Component c in need)
                        {
                            bool hit = false;
                            foreach (FileEntry f in kv.Value)
                                if (string.Equals(Path.GetFileName(f.Name), c.SaveAs, StringComparison.OrdinalIgnoreCase)) { hit = true; break; }
                            if (!hit) miss.Add(c.SaveAs);
                        }
                        if (miss.Count == 0) { complete = true; missing.Clear(); }
                        else if (missing.Count == 0 || miss.Count < missing.Count) { missing = miss; }
                    }
                }
                if (!complete)
                    warn.Add("这是图片/视频模型组件（" + kv.Key + "）：单独下载不能运行；建议用「图片/视频配方下载」一次下齐三件套" +
                             (missing.Count > 0 ? "（还缺：" + string.Join("、", missing.ToArray()) + "）" : "") + "。");
            }

            Dictionary<string, int> quantHits = new Dictionary<string, int>();
            foreach (FileEntry f in selected)
            {
                if (f.Name.IndexOf("mmproj", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                string baseName = Path.GetFileNameWithoutExtension(f.Name);
                baseName = Regex.Replace(baseName, @"[-._](?:Q\d[_A-Za-z0-9]*|[Ii]Q\d[_A-Za-z0-9]*|f16|bf16|fp16)$", "", RegexOptions.IgnoreCase);
                if (!quantHits.ContainsKey(baseName)) quantHits[baseName] = 0;
                quantHits[baseName]++;
            }
            foreach (KeyValuePair<string, int> kv in quantHits)
                if (kv.Value > 1)
                    warn.Add("同一模型勾了 " + kv.Value + " 个量化（" + kv.Key + "），会重复占用磁盘，通常只需一个。");

            if (checkUrls && src != Source.Civitai)
            {
                int checkedCount = 0;
                foreach (FileEntry f in selected)
                {
                    if (checkedCount >= 12)
                    {
                        warn.Add("勾选文件较多，只校验了前 12 个链接，其余未检查。");
                        break;
                    }
                    checkedCount++;
                    int st = string.IsNullOrEmpty(f.Url) ? HeadStatus(src, mirror, repo, f.Name) : HeadUrl(f.Url);
                    if (st == 401 || st == 403)
                        warn.Add("需要访问令牌才能下载：" + f.Name + "（HTTP " + st + "，可在「Token 设置」填对应令牌）");
                    else if (st == 0 || st >= 400)
                        warn.Add("取不到这个文件：" + f.Name + "（HTTP " + st + "）");
                }
            }
            return warn;
        }

        public static List<Partial> ScanPartials(string modelsDir)
        {
            List<Partial> list = new List<Partial>();
            try
            {
                if (!Directory.Exists(modelsDir)) return list;

                foreach (string d in Directory.GetDirectories(modelsDir))
                {
                    string name = Path.GetFileName(d);
                    if (string.Equals(name, "image", StringComparison.OrdinalIgnoreCase)) continue;
                    int idx = name.IndexOf("__");
                    if (idx <= 0) continue;
                    foreach (string p in Directory.GetFiles(d, "*.part"))
                    {
                        string fn = Path.GetFileName(p);
                        Partial pt = new Partial();
                        pt.Dir = d;
                        pt.FileName = fn.Substring(0, fn.Length - 5);
                        pt.Repo = name.Substring(0, idx) + "/" + name.Substring(idx + 2);
                        pt.SubPath = pt.FileName;
                        pt.SourceNote = "文字/对话模型";
                        list.Add(pt);
                    }
                }

                string img = Path.Combine(modelsDir, "image");
                if (Directory.Exists(img))
                {
                    List<Recipe> recipes = LoadRecipes();
                    foreach (string d in Directory.GetDirectories(img))
                    {
                        string id = Path.GetFileName(d);
                        Recipe rec = null;
                        foreach (Recipe r in recipes) if (r.Id == id) rec = r;
                        foreach (string p in Directory.GetFiles(d, "*.part"))
                        {
                            string fn = Path.GetFileName(p);
                            fn = fn.Substring(0, fn.Length - 5);
                            Partial pt = null;
                            if (rec != null)
                            {
                                List<Component> all = new List<Component>();
                                foreach (List<Component> g in rec.Quants) all.AddRange(g);
                                all.AddRange(rec.Fixed);
                                foreach (Component c in all)
                                    if (c.SaveAs == fn) { pt = new Partial(); pt.Repo = c.Repo; pt.SubPath = c.Path; break; }
                            }
                            if (pt == null) continue;
                            pt.Dir = d;
                            pt.FileName = fn;
                            pt.SourceNote = "图片/视频配方：" + (rec != null ? rec.Title : id);
                            list.Add(pt);
                        }
                    }
                }
            }
            catch { }
            return list;
        }

        // ---------- 配方（外置 recipes.json） ----------

        public static string RecipeFile()
        {
            string dir = DataDir();
            if (dir == null) return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "recipes.json");
            return Path.Combine(dir, "recipes.json");
        }

        static void MigrateLegacyRecipeFile()
        {
            try
            {
                string legacy = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "recipes.json");
                string target = RecipeFile();
                if (!File.Exists(target) && File.Exists(legacy)) File.Copy(legacy, target, false);
            }
            catch { }
        }

        static Component Comp(string repo, string path)
        {
            Component c = new Component();
            c.Repo = repo;
            c.Path = path;
            int i = path.LastIndexOf('/');
            c.SaveAs = i >= 0 ? path.Substring(i + 1) : path;
            return c;
        }

        public static List<Recipe> BuiltinRecipes()
        {
            List<Recipe> list = new List<Recipe>();
            Recipe r;

            r = new Recipe(); r.Id = "z-image-turbo"; r.Title = "Z-Image Turbo";
            r.QuantLabels.AddRange(new string[] { "Q8_0", "Q6_K", "Q4_K", "Q3_K" });
            foreach (string q in r.QuantLabels) r.Quants.Add(new List<Component> { Comp("leejet/Z-Image-Turbo-GGUF", "z_image_turbo-" + q + ".gguf") });
            r.Fixed.Add(Comp("unsloth/Qwen3-4B-Instruct-2507-GGUF", "Qwen3-4B-Instruct-2507-Q4_K_M.gguf"));
            r.Fixed.Add(Comp("Comfy-Org/z_image_turbo", "split_files/vae/ae.safetensors"));
            list.Add(r);

            r = new Recipe(); r.Id = "qwen-image"; r.Title = "Qwen-Image 文生图";
            r.QuantLabels.Add("Q8_0");
            r.Quants.Add(new List<Component> { Comp("QuantStack/Qwen-Image-GGUF", "Qwen_Image-Q8_0.gguf") });
            r.Fixed.Add(Comp("mradermacher/Qwen2.5-VL-7B-Instruct-GGUF", "Qwen2.5-VL-7B-Instruct.Q4_K_M.gguf"));
            r.Fixed.Add(Comp("Comfy-Org/Qwen-Image_ComfyUI", "split_files/vae/qwen_image_vae.safetensors"));
            list.Add(r);

            r = new Recipe(); r.Id = "flux1-schnell"; r.Title = "FLUX.1 schnell";
            r.QuantLabels.Add("Q8_0");
            r.Quants.Add(new List<Component> { Comp("leejet/FLUX.1-schnell-gguf", "flux1-schnell-q8_0.gguf") });
            r.Fixed.Add(Comp("comfyanonymous/flux_text_encoders", "clip_l.safetensors"));
            r.Fixed.Add(Comp("comfyanonymous/flux_text_encoders", "t5xxl_fp16.safetensors"));
            list.Add(r);

            r = new Recipe(); r.Id = "flux2-klein-4b"; r.Title = "FLUX.2 klein 4B";
            r.QuantLabels.Add("Q8_0");
            r.Quants.Add(new List<Component> { Comp("leejet/FLUX.2-klein-4B-GGUF", "flux-2-klein-4b-Q8_0.gguf") });
            r.Fixed.Add(Comp("unsloth/Qwen3-4B-GGUF", "Qwen3-4B-Q4_K_M.gguf"));
            r.Fixed.Add(Comp("Comfy-Org/flux2-klein-4B", "split_files/vae/flux2-vae.safetensors"));
            list.Add(r);

            r = new Recipe(); r.Id = "qwen-image-edit"; r.Title = "Qwen-Image-Edit 改图";
            r.QuantLabels.Add("Q8_0");
            r.Quants.Add(new List<Component> { Comp("unsloth/Qwen-Image-Edit-2511-GGUF", "qwen-image-edit-2511-Q8_0.gguf") });
            r.Fixed.Add(Comp("mradermacher/Qwen2.5-VL-7B-Instruct-GGUF", "Qwen2.5-VL-7B-Instruct.mmproj-Q8_0.gguf"));
            r.Fixed.Add(Comp("Comfy-Org/Qwen-Image_ComfyUI", "split_files/vae/qwen_image_vae.safetensors"));
            list.Add(r);

            r = new Recipe(); r.Id = "wan2.1-t2v-1.3b"; r.Title = "Wan2.1 文生视频 1.3B";
            r.QuantLabels.Add("fp16");
            r.Quants.Add(new List<Component> { Comp("Comfy-Org/Wan_2.1_ComfyUI_repackaged", "split_files/diffusion_models/wan2.1_t2v_1.3B_fp16.safetensors") });
            r.Fixed.Add(Comp("city96/umt5-xxl-encoder-gguf", "umt5-xxl-encoder-Q8_0.gguf"));
            r.Fixed.Add(Comp("Comfy-Org/Wan_2.1_ComfyUI_repackaged", "split_files/vae/wan_2.1_vae.safetensors"));
            list.Add(r);

            r = new Recipe(); r.Id = "wan2.2-ti2v-5b"; r.Title = "Wan2.2 文生视频 5B";
            r.QuantLabels.Add("Q8_0");
            r.Quants.Add(new List<Component> { Comp("QuantStack/Wan2.2-TI2V-5B-GGUF", "Wan2.2-TI2V-5B-Q8_0.gguf") });
            r.Fixed.Add(Comp("city96/umt5-xxl-encoder-gguf", "umt5-xxl-encoder-Q8_0.gguf"));
            r.Fixed.Add(Comp("Comfy-Org/Wan_2.2_ComfyUI_Repackaged", "split_files/vae/wan2.2_vae.safetensors"));
            list.Add(r);

            r = new Recipe(); r.Id = "wan2.2-i2v-a14b"; r.Title = "Wan2.2 图生视频 A14B";
            r.QuantLabels.Add("Q4_K_M");
            r.Quants.Add(new List<Component> {
                Comp("QuantStack/Wan2.2-I2V-A14B-GGUF", "LowNoise/Wan2.2-I2V-A14B-LowNoise-Q4_K_M.gguf"),
                Comp("QuantStack/Wan2.2-I2V-A14B-GGUF", "HighNoise/Wan2.2-I2V-A14B-HighNoise-Q4_K_M.gguf") });
            r.Fixed.Add(Comp("city96/umt5-xxl-encoder-gguf", "umt5-xxl-encoder-Q8_0.gguf"));
            r.Fixed.Add(Comp("Comfy-Org/Wan_2.2_ComfyUI_Repackaged", "split_files/vae/wan2.2_vae.safetensors"));
            list.Add(r);

            r = new Recipe(); r.Id = "ltx-2.3"; r.Title = "LTX-2.3 视频";
            r.QuantLabels.Add("Q4_K_M");
            r.Quants.Add(new List<Component> { Comp("unsloth/LTX-2.3-GGUF", "distilled-1.1/ltx-2.3-22b-distilled-1.1-Q4_K_M.gguf") });
            r.Fixed.Add(Comp("unsloth/gemma-3-12b-it-qat-GGUF", "gemma-3-12b-it-qat-UD-Q4_K_XL.gguf"));
            r.Fixed.Add(Comp("unsloth/LTX-2.3-GGUF", "text_encoders/ltx-2.3-22b-distilled_embeddings_connectors.safetensors"));
            r.Fixed.Add(Comp("unsloth/LTX-2.3-GGUF", "vae/ltx-2.3-22b-distilled_video_vae.safetensors"));
            r.Fixed.Add(Comp("unsloth/LTX-2.3-GGUF", "vae/ltx-2.3-22b-distilled_audio_vae.safetensors"));
            list.Add(r);

            r = new Recipe(); r.Id = "minimax-h3"; r.Title = "MiniMax-H3 视频";
            r.QuantLabels.Add("Q4_K");
            r.Quants.Add(new List<Component> { Comp("unsloth/MiniMax-H3-GGUF", "minimax_h3_fl2va_pruned-Q4_K.gguf") });
            r.Fixed.Add(Comp("unsloth/MiniMax-H3-GGUF", "qwen3vl_32b_minimax_h3-Q4_K_M.gguf"));
            r.Fixed.Add(Comp("unsloth/MiniMax-H3-GGUF", "vae/minimax_h3_video_vae_fp16.safetensors"));
            r.Fixed.Add(Comp("unsloth/MiniMax-H3-GGUF", "vae/minimax_h3_audio_vae_fp32.safetensors"));
            list.Add(r);

            return list;
        }

        public static void SaveRecipes(List<Recipe> recipes)
        {
            try
            {
                List<Dictionary<string, object>> outList = new List<Dictionary<string, object>>();
                foreach (Recipe r in recipes)
                {
                    Dictionary<string, object> d = new Dictionary<string, object>();
                    d["id"] = r.Id;
                    d["title"] = r.Title;
                    List<object> quants = new List<object>();
                    for (int i = 0; i < r.QuantLabels.Count; i++)
                    {
                        Dictionary<string, object> q = new Dictionary<string, object>();
                        q["label"] = r.QuantLabels[i];
                        List<object> files = new List<object>();
                        foreach (Component c in r.Quants[i])
                        {
                            Dictionary<string, object> f = new Dictionary<string, object>();
                            f["repo"] = c.Repo; f["path"] = c.Path;
                            files.Add(f);
                        }
                        q["files"] = files;
                        quants.Add(q);
                    }
                    d["quants"] = quants;
                    List<object> fixedFiles = new List<object>();
                    foreach (Component c in r.Fixed)
                    {
                        Dictionary<string, object> f = new Dictionary<string, object>();
                        f["repo"] = c.Repo; f["path"] = c.Path;
                        fixedFiles.Add(f);
                    }
                    d["fixed"] = fixedFiles;
                    outList.Add(d);
                }
                JavaScriptSerializer js = new JavaScriptSerializer();
                File.WriteAllText(RecipeFile(), js.Serialize(outList), new UTF8Encoding(false));
            }
            catch (Exception ex) { Log("保存配方失败: " + ex.Message); }
        }

        public static List<Recipe> LoadRecipes()
        {
            string path = RecipeFile();
            if (!File.Exists(path))
            {
                MigrateLegacyRecipeFile();
                if (!File.Exists(path))
                {
                    List<Recipe> built = BuiltinRecipes();
                    SaveRecipes(built);
                    return built;
                }
            }
            try
            {
                string text = File.ReadAllText(path, Encoding.UTF8);
                JavaScriptSerializer js = new JavaScriptSerializer();
                js.MaxJsonLength = 32 * 1024 * 1024;
                object[] arr = (object[])js.DeserializeObject(text);
                List<Recipe> list = new List<Recipe>();
                foreach (object o in arr)
                {
                    Dictionary<string, object> d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    Recipe r = new Recipe();
                    r.Id = d.ContainsKey("id") ? Convert.ToString(d["id"]) : "";
                    r.Title = d.ContainsKey("title") ? Convert.ToString(d["title"]) : r.Id;
                    if (d.ContainsKey("quants") && d["quants"] != null)
                    {
                        System.Collections.IEnumerable qs = d["quants"] as System.Collections.IEnumerable;
                        if (qs != null)
                        foreach (object qo in qs)
                        {
                            Dictionary<string, object> q = qo as Dictionary<string, object>;
                            if (q == null) continue;
                            r.QuantLabels.Add(q.ContainsKey("label") ? Convert.ToString(q["label"]) : "default");
                            List<Component> comps = new List<Component>();
                            if (q.ContainsKey("files") && q["files"] != null)
                            {
                                System.Collections.IEnumerable fcs = q["files"] as System.Collections.IEnumerable;
                                if (fcs != null)
                                foreach (object fo in fcs)
                                {
                                    Dictionary<string, object> f = fo as Dictionary<string, object>;
                                    if (f == null) continue;
                                    comps.Add(Comp(Convert.ToString(f["repo"]), Convert.ToString(f["path"])));
                                }
                            }
                            r.Quants.Add(comps);
                        }
                    }
                    if (d.ContainsKey("fixed") && d["fixed"] != null)
                    {
                        System.Collections.IEnumerable fxs = d["fixed"] as System.Collections.IEnumerable;
                        if (fxs != null)
                        foreach (object fo in fxs)
                        {
                            Dictionary<string, object> f = fo as Dictionary<string, object>;
                            if (f == null) continue;
                            r.Fixed.Add(Comp(Convert.ToString(f["repo"]), Convert.ToString(f["path"])));
                        }
                    }
                    if (r.Id.Length > 0 && r.Quants.Count > 0) list.Add(r);
                }
                if (list.Count > 0) return list;
            }
            catch (Exception ex)
            {
                Log("recipes.json 解析失败，改用内置配方: " + ex.Message);
            }
            return BuiltinRecipes();
        }
    }

    class TokenForm : Form
    {
        TextBox txtHf, txtMs, txtCv;

        public TokenForm()
        {
            Text = "访问令牌设置";
            Width = 580; Height = 300;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            Font = new Font("Microsoft YaHei UI", 9f);
            Core.ApplyIcon(this);

            Label l1 = new Label(); l1.Text = "HuggingFace token（下载 gated 仓库时才需要）"; l1.SetBounds(16, 14, 500, 20);
            txtHf = new TextBox(); txtHf.SetBounds(16, 36, 540, 24);
            txtHf.UseSystemPasswordChar = true; txtHf.Text = Secrets.Hf;

            Label l2 = new Label(); l2.Text = "ModelScope token（下载受限仓库时才需要）"; l2.SetBounds(16, 66, 500, 20);
            txtMs = new TextBox(); txtMs.SetBounds(16, 88, 540, 24);
            txtMs.UseSystemPasswordChar = true; txtMs.Text = Secrets.Ms;

            Label l3 = new Label(); l3.Text = "Civitai API key（下载受限/成人内容时需要）"; l3.SetBounds(16, 118, 500, 20);
            txtCv = new TextBox(); txtCv.SetBounds(16, 140, 540, 24);
            txtCv.UseSystemPasswordChar = true; txtCv.Text = Secrets.Civitai;

            Label hint = new Label();
            hint.Text = "令牌用 Windows DPAPI 加密保存在本机，不会外传。留空表示不使用。";
            hint.SetBounds(16, 172, 540, 20);
            hint.ForeColor = Color.Gray;

            Button ok = new Button(); ok.Text = "保存"; ok.SetBounds(360, 210, 90, 28);
            ok.Click += delegate
            {
                Secrets.Hf = txtHf.Text.Trim();
                Secrets.Ms = txtMs.Text.Trim();
                Secrets.Civitai = txtCv.Text.Trim();
                if (!Secrets.Save())
                {
                    MessageBox.Show(this, "令牌保存失败：数据目录不可写。\r\n（便携版请确认程序目录可写）", "保存失败",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;   // 保存失败就不关窗，别让调用方以为已经存好了
                }
                DialogResult = DialogResult.OK;
            };
            Button cancel = new Button(); cancel.Text = "取消"; cancel.SetBounds(466, 210, 90, 28);
            cancel.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[] { l1, txtHf, l2, txtMs, l3, txtCv, hint, ok, cancel });
            AcceptButton = ok;
            CancelButton = cancel;
        }
    }

    class RecipeForm : Form
    {
        ComboBox cboRecipe, cboQuant;
        Label lblTarget, lblHint;
        Button btnGo, btnClose;
        List<Core.Recipe> recipes;
        string modelsDir, mirror;
        Action<string> log;

        public Core.JobItem ResultJob = null;
        public string ResultTitle = "";

        public RecipeForm(string modelsDir, string mirror, Action<string> log, string selectId)
        {
            this.modelsDir = modelsDir;
            this.mirror = mirror;
            this.log = log;
            this.recipes = Core.LoadRecipes();

            Text = "图片 / 视频模型配方下载（aria2）";
            Width = 720; Height = 300;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            Font = new Font("Microsoft YaHei UI", 9f);
            Core.ApplyIcon(this);

            Label l1 = new Label(); l1.Text = "模型"; l1.SetBounds(16, 16, 60, 20);
            cboRecipe = new ComboBox(); cboRecipe.SetBounds(16, 38, 430, 24);
            cboRecipe.DropDownStyle = ComboBoxStyle.DropDownList;

            Label l2 = new Label(); l2.Text = "量化"; l2.SetBounds(460, 16, 60, 20);
            cboQuant = new ComboBox(); cboQuant.SetBounds(460, 38, 170, 24);
            cboQuant.DropDownStyle = ComboBoxStyle.DropDownList;

            lblTarget = new Label(); lblTarget.SetBounds(16, 72, 680, 20);
            lblHint = new Label(); lblHint.SetBounds(16, 96, 620, 34);
            lblHint.ForeColor = Color.Gray;
            lblHint.Text = "下载内容 = 主体模型 + 文本编码器 + VAE，全部用 aria2 16 线程。\r\n下载前请先在 LocModel 里暂停同名下载，避免冲突。";

            btnGo = new Button(); btnGo.Text = "开始下载"; btnGo.SetBounds(400, 160, 110, 30);
            btnGo.Click += delegate { Go(); };
            Button btnEdit = new Button(); btnEdit.Text = "编辑配方文件"; btnEdit.SetBounds(280, 160, 110, 30);
            btnEdit.Click += delegate
            {
                try
                {
                    Core.LoadRecipes();
                    Process.Start("notepad.exe", "\"" + Core.RecipeFile() + "\"");
                }
                catch (Exception ex) { MessageBox.Show(this, "打不开配方文件：" + ex.Message, "提示"); }
            };
            btnClose = new Button(); btnClose.Text = "关闭"; btnClose.SetBounds(520, 160, 110, 30);
            btnClose.Click += delegate { Close(); };

            Controls.AddRange(new Control[] { l1, cboRecipe, l2, cboQuant, lblTarget, lblHint, btnGo, btnEdit, btnClose });

            foreach (Core.Recipe r in recipes) cboRecipe.Items.Add(r);
            cboRecipe.SelectedIndexChanged += delegate { RefreshQuant(); };
            if (cboRecipe.Items.Count > 0) cboRecipe.SelectedIndex = 0;
            if (!string.IsNullOrEmpty(selectId))
            {
                for (int i = 0; i < recipes.Count; i++)
                    if (recipes[i].Id == selectId) { cboRecipe.SelectedIndex = i; break; }
            }
            RefreshQuant();
        }

        void RefreshQuant()
        {
            cboQuant.Items.Clear();
            Core.Recipe r = cboRecipe.SelectedItem as Core.Recipe;
            if (r == null) return;
            foreach (string q in r.QuantLabels) cboQuant.Items.Add(q);
            if (cboQuant.Items.Count > 0) cboQuant.SelectedIndex = 0;
            lblTarget.Text = "主体 -> " + Core.RecipeFolder(modelsDir, r.Id) +
                             "    编码器/VAE -> " + Core.CommonFolder(modelsDir);
        }

        void Say(string s)
        {
            if (log != null) log(s);
        }

        async void Go()
        {
            Core.Recipe r = cboRecipe.SelectedItem as Core.Recipe;
            if (r == null || cboQuant.SelectedIndex < 0) return;
            int qi = cboQuant.SelectedIndex;   // 固定住，后面 await 期间用户改了选项也不会错配

            List<Core.Component> items = new List<Core.Component>();
            items.AddRange(r.Quants[qi]);
            items.AddRange(r.Fixed);

            btnGo.Enabled = false; btnClose.Enabled = false;
            cboRecipe.Enabled = false; cboQuant.Enabled = false;
            lblHint.Text = "正在检查配方文件是否都能取到…";
            List<string> bad = new List<string>();
            try
            {
                string mir = mirror;
                bad = await Task.Run(() =>
                {
                    List<string> miss = new List<string>();
                    foreach (Core.Component c in items)
                        if (!Core.HeadOk(Source.HF, mir, c.Repo, c.Path))
                            miss.Add(c.SaveAs + "   (" + c.Repo + ")");
                    return miss;
                });
            }
            catch { }
            if (IsDisposed) return;
            btnGo.Enabled = true; btnClose.Enabled = true;
            cboRecipe.Enabled = true; cboQuant.Enabled = true;
            lblHint.Text = "下载内容 = 主体模型 + 文本编码器 + VAE，全部用 aria2 16 线程。\r\n下载前请先在 LocModel 里暂停同名下载，避免冲突。";

            if (bad.Count > 0)
            {
                StringBuilder mb = new StringBuilder();
                mb.AppendLine("配方检查失败，以下文件取不到：");
                mb.AppendLine();
                foreach (string b in bad) mb.AppendLine("- " + b);
                mb.AppendLine();
                mb.Append("已中止下载。");
                MessageBox.Show(this, mb.ToString(), "配方检查失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            StringBuilder names = new StringBuilder();
            foreach (Core.Component c in items) names.Append("  - ").Append(c.SaveAs).Append("\r\n");
            if (MessageBox.Show(this, "将下载以下文件到：\r\n" + Core.RecipeFolder(modelsDir, r.Id) + "\r\n\r\n" + names,
                "确认", MessageBoxButtons.OKCancel) != DialogResult.OK) return;

            ResultJob = Core.BuildRecipeJob(r, qi, modelsDir);
            ResultTitle = r.Title;
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    class DirForm : Form
    {
        public string NewDir;
        Label lblPath;
        string dir;

        public DirForm(string current)
        {
            dir = current;
            Text = "模型目录";
            Width = 620; Height = 185;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            Font = new Font("Microsoft YaHei UI", 9f);
            Core.ApplyIcon(this);

            Label l1 = new Label(); l1.Text = "当前保存位置："; l1.SetBounds(16, 14, 200, 20);
            lblPath = new Label(); lblPath.SetBounds(16, 36, 580, 40);
            lblPath.Text = current;
            lblPath.AutoEllipsis = true;

            Button open = new Button(); open.Text = "打开文件夹"; open.SetBounds(16, 96, 120, 30);
            open.Click += delegate { OpenDir(); };

            Button change = new Button(); change.Text = "更改目录…"; change.SetBounds(146, 96, 120, 30);
            change.Click += delegate
            {
                using (FolderBrowserDialog dlg = new FolderBrowserDialog())
                {
                    dlg.Description = "选择 LocModel 的 models 文件夹";
                    try { dlg.SelectedPath = dir; } catch { }
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                    {
                        dir = dlg.SelectedPath;
                        NewDir = dir;
                        lblPath.Text = dir;
                    }
                }
            };

            Button ok = new Button(); ok.Text = "确定"; ok.SetBounds(486, 96, 110, 30);
            ok.DialogResult = DialogResult.OK;
            AcceptButton = ok;

            Controls.AddRange(new Control[] { l1, lblPath, open, change, ok });
        }

        void OpenDir()
        {
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = dir;
                psi.UseShellExecute = true;
                psi.Verb = "open";
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "打不开目录：\r\n" + dir + "\r\n\r\n" + ex.Message, "提示");
            }
        }
    }

    class QueueForm : Form
    {
        ListBox lst;
        TextBox txtAdd;
        Label lblStatus, lblHint;
        Button btnAdd, btnStart, btnPause, btnSkip, btnRemove, btnClose;
        List<Core.JobItem> jobs = new List<Core.JobItem>();
        string modelsDir, mirror;
        volatile bool pauseFlag, skipFlag;
        bool running;
        bool parsing;
        Core.JobItem current = null;

        public QueueForm(string modelsDir, string mirror, string prefill)
        {
            this.modelsDir = modelsDir;
            this.mirror = mirror;

            Text = "批量队列（aria2 断点续传）";
            Width = 760; Height = 560;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            Font = new Font("Microsoft YaHei UI", 9f);
            Core.ApplyIcon(this);

            Label l1 = new Label(); l1.Text = "任务队列（按顺序下载）"; l1.SetBounds(16, 12, 400, 20);
            lst = new ListBox(); lst.SetBounds(16, 34, 716, 190);
            lst.HorizontalScrollbar = true;

            Label l2 = new Label(); l2.Text = "添加任务：每行一个，支持三种写法"; l2.SetBounds(16, 232, 400, 20);
            lblHint = new Label();
            lblHint.SetBounds(16, 252, 716, 34);
            lblHint.ForeColor = Color.Gray;
            lblHint.Text = "owner/repo（HF）｜ ms:owner/repo（魔塔）｜ cv:版本ID 或 C站模型链接（C站）——自动选量化或全部文件\r\n" +
                           "owner/repo :: 文件名（指定单个文件）｜ recipe:配方id 量化（图片/视频模型）";

            txtAdd = new TextBox(); txtAdd.SetBounds(16, 290, 716, 60);
            txtAdd.Multiline = true; txtAdd.ScrollBars = ScrollBars.Vertical;
            txtAdd.Font = new Font("Consolas", 9f);
            if (!string.IsNullOrEmpty(prefill)) txtAdd.Text = prefill;

            btnAdd = new Button(); btnAdd.Text = "添加到队列"; btnAdd.SetBounds(16, 358, 110, 28);
            btnAdd.Click += delegate { AddLines(); };
            btnStart = new Button(); btnStart.Text = "开始 / 继续"; btnStart.SetBounds(136, 358, 110, 28);
            btnStart.Click += delegate { StartQueue(); };
            btnPause = new Button(); btnPause.Text = "暂停全部"; btnPause.SetBounds(256, 358, 100, 28);
            btnPause.Enabled = false;
            btnPause.Click += delegate
            {
                pauseFlag = true;
                lblStatus.Text = "正在暂停（保存断点）…";
            };
            btnSkip = new Button(); btnSkip.Text = "取消选中项"; btnSkip.SetBounds(366, 358, 110, 28);
            btnSkip.Click += delegate { SkipSelected(); };
            btnRemove = new Button(); btnRemove.Text = "移除选中项"; btnRemove.SetBounds(486, 358, 110, 28);
            btnRemove.Click += delegate { RemoveSelected(); };
            btnClose = new Button(); btnClose.Text = "关闭"; btnClose.SetBounds(632, 358, 100, 28);
            btnClose.Click += delegate
            {
                if (running) { MessageBox.Show(this, "请先点「暂停全部」，再关闭。", "提示"); return; }
                Close();
            };

            lblStatus = new Label(); lblStatus.SetBounds(16, 396, 716, 20);
            lblStatus.Text = "就绪";
            lblStatus.ForeColor = Color.FromArgb(20, 90, 160);

            Label l3 = new Label();
            l3.SetBounds(16, 424, 716, 80);
            l3.ForeColor = Color.Gray;
            l3.Text = "说明：每次下载失败/暂停都会保留 aria2 的 .aria2 断点文件，下次点「开始 / 继续」会从断点续传；\r\n" +
                      "关闭工具再打开，队列会保留，并会提示你继续上次没下完的任务。\r\n" +
                      "下载前会检查磁盘剩余空间，不足会拦下来。";

            Controls.AddRange(new Control[] { l1, lst, l2, lblHint, txtAdd, btnAdd, btnStart, btnPause, btnSkip, btnRemove, btnClose, lblStatus, l3 });

            jobs = Core.LoadQueue();
            RefreshList();
            if (jobs.Count > 0) lblStatus.Text = "已恢复上次队列（" + jobs.Count + " 项）";
        }

        void RefreshList()
        {
            int sel = lst.SelectedIndex;
            lst.Items.Clear();
            foreach (Core.JobItem j in jobs) lst.Items.Add(j);
            if (sel >= 0 && sel < lst.Items.Count) lst.SelectedIndex = sel;
            Core.SaveQueue(jobs);
        }

        void Say(string s)
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired) { try { BeginInvoke(new Action<string>(Say), s); } catch { } return; }
            lblStatus.Text = s;
            Regex r = new Regex(@"\[#\w+\s+([^\]]+)\]");
            Match m = r.Match(s);
            if (m.Success) lblStatus.Text = "下载中　" + m.Groups[1].Value;
        }

        async void AddLines()
        {
            string[] lines = txtAdd.Text.Replace("\r", "").Split('\n');
            btnAdd.Enabled = false;
            parsing = true;
            int added = 0;
            try
            {
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                try
                {
                    Core.JobItem job = null;
                    if (line.StartsWith("recipe:", StringComparison.OrdinalIgnoreCase))
                    {
                        string rest = line.Substring(7).Trim();
                        string id = rest, quant = "";
                        int sp = rest.IndexOf(' ');
                        if (sp > 0) { id = rest.Substring(0, sp); quant = rest.Substring(sp + 1).Trim(); }
                        Say("解析配方 " + id + " …");
                        string cid = id, cq = quant;
                        job = await Task.Run(() => Core.ResolveRecipeJob(cid, cq, modelsDir));
                        if (job.Files.Count == 0) { Say("没有这个配方：" + id); continue; }
                    }
                    else
                    {
                        string repoLine = line, file = "";
                        int p = line.IndexOf("::");
                        if (p > 0) { repoLine = line.Substring(0, p).Trim(); file = line.Substring(p + 2).Trim(); }
                        Source s2; string repo2; string hint;
                        Core.ParseRepoInput(repoLine, Source.HF, out s2, out repo2, out hint);
                        if (string.IsNullOrEmpty(file) && !string.IsNullOrEmpty(hint)) file = hint;   // 粘文件页链接时自动定位文件
                        Say("解析 " + repo2 + " …");
                        string r2 = repo2, f2 = file;
                        Source ss = s2;
                        job = await Task.Run(() => Core.ResolveRepoJob(ss, mirror, r2, f2, modelsDir));
                        if (job.Files.Count == 0)
                        {
                            Say("没解析出文件：" + line + "（该仓库可能没有 .gguf，请用 owner/repo :: 文件名 指定，或回主界面勾选）");
                            continue;
                        }
                        if (ss == Source.HF)
                        {
                            string recName = Core.RecipeNameForComponent(job.Repo, job.Files[0].SaveAs);
                            if (recName != null)
                            {
                                Say("跳过：" + recName + " 是图片/视频模型，请用 recipe:<配方id> <量化> 的形式添加");
                                continue;
                            }
                        }
                    }
                    bool dup = false;
                    foreach (Core.JobItem e in jobs)
                    {
                        if (Core.SameJob(e, job)) { dup = true; break; }
                    }
                    if (dup) { Say("已在队列中，跳过：" + job.Title); continue; }
                    jobs.Add(job);
                    added++;
                }
                catch (Exception ex) { Say("解析失败：" + ex.Message); }
            }
            }
            finally
            {
                parsing = false;
                if (!IsDisposed && !Disposing && btnAdd != null) btnAdd.Enabled = true;
            }
            if (IsDisposed || Disposing) return;
            txtAdd.Clear();
            RefreshList();
            lblStatus.Text = "已添加 " + added + " 项，共 " + jobs.Count + " 项";
        }

        void SkipSelected()
        {
            int i = lst.SelectedIndex;
            if (i < 0 || i >= jobs.Count) { MessageBox.Show(this, "先在列表里选一项。", "提示"); return; }
            Core.JobItem j = jobs[i];
            if (j == current && running) { skipFlag = true; lblStatus.Text = "正在取消当前项…"; return; }
            if (j.Status == "done") { MessageBox.Show(this, "这项已经下完了。", "提示"); return; }
            j.Status = "cancelled";
            RefreshList();
        }

        void RemoveSelected()
        {
            int i = lst.SelectedIndex;
            if (i < 0 || i >= jobs.Count) return;
            Core.JobItem j = jobs[i];
            if (j == current && running) { MessageBox.Show(this, "正在下载，先取消它再移除。", "提示"); return; }
            jobs.RemoveAt(i);
            RefreshList();
        }

        async void StartQueue()
        {
            if (running) return;
            List<Core.JobItem> todo = new List<Core.JobItem>();
            foreach (Core.JobItem j in jobs)
                if (j.Status != "done" && j.Status != "cancelled") todo.Add(j);
            if (todo.Count == 0) { MessageBox.Show(this, "没有待下载的任务。", "提示"); return; }

            long total = 0;
            foreach (Core.JobItem j in todo) total += j.TotalSize;
            long free = Core.FreeSpace(modelsDir);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("即将按顺序下载 " + todo.Count + " 项：");
            sb.AppendLine();
            foreach (Core.JobItem j in todo) sb.AppendLine("  - " + j.Title + "   " + Core.Fmt(j.TotalSize));
            sb.AppendLine();
            sb.AppendLine("合计：" + Core.Fmt(total));
            if (free >= 0) sb.AppendLine("磁盘剩余：" + Core.Fmt(free) + "（" + Path.GetPathRoot(Path.GetFullPath(modelsDir)) + "）");
            if (free >= 0 && free < total)
            {
                MessageBox.Show(this, "磁盘空间不足，已中止。\r\n\r\n需要：" + Core.Fmt(total) + "\r\n剩余：" + Core.Fmt(free),
                    "空间不足", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (MessageBox.Show(this, sb.ToString() + "\r\n确认开始下载吗？", "批量下载确认", MessageBoxButtons.OKCancel,
                MessageBoxIcon.Information) != DialogResult.OK) return;

            running = true; pauseFlag = false; skipFlag = false;
            btnStart.Enabled = false; btnPause.Enabled = true; btnAdd.Enabled = false;
            btnRemove.Enabled = false;

            try
            {
                for (int i = 0; i < jobs.Count; i++)
                {
                    Core.JobItem j = jobs[i];
                    if (j.Status == "done" || j.Status == "cancelled") continue;
                    if (pauseFlag) break;
                    current = j;
                    j.Status = "running";
                    RefreshList();

                    foreach (Core.JobFile f in j.Files)
                    {
                        if (pauseFlag) break;
                        if (skipFlag) break;
                        Say("=== " + j.Title + " :: " + f.SaveAs);
                        string err = null;
                        Source jsrc = j.Src == "ms" ? Source.MS : (j.Src == "cv" ? Source.Civitai : Source.HF);
                        int rc = await Task.Run(() =>
                        {
                            string inner;
                            int r = Core.RunAriaTo(jsrc, mirror, f.Repo, f.Path, f.SaveAs, f.Dir, Say,
                                delegate { return pauseFlag || skipFlag; }, out inner, f.Url, f.Size, f.Hash);
                            err = inner;
                            return r;
                        });
                        if (rc == -2) break;
                        if (rc != 0) { j.Status = "failed"; Say("=== 失败：" + f.SaveAs + "  " + (err ?? "")); break; }
                    }

                    if (skipFlag)
                    {
                        if (j.Status != "failed") j.Status = "cancelled";
                        skipFlag = false;
                    }
                    else if (pauseFlag)
                    {
                        if (j.Status != "failed") j.Status = "paused";
                    }
                    else
                    {
                        if (j.Status != "failed") j.Status = "done";
                    }
                    RefreshList();
                    if (pauseFlag) break;
                }
            }
            catch (Exception ex)
            {
                Say("=== 队列异常：" + ex.Message);
                try { Core.Log("队列异常: " + ex); } catch { }
            }
            finally
            {
                running = false; current = null;
                btnStart.Enabled = true; btnPause.Enabled = false; btnAdd.Enabled = true; btnRemove.Enabled = true;
                Core.SaveQueue(jobs);
                if (pauseFlag) lblStatus.Text = "已暂停（断点已保存，下次可继续）";
                else lblStatus.Text = "队列结束";
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (running)
            {
                e.Cancel = true;
                MessageBox.Show(this, "正在下载，先点「暂停全部」再关闭。", "提示");
                return;
            }
            if (parsing)
            {
                e.Cancel = true;
                MessageBox.Show(this, "正在解析仓库（联网查文件列表），等它结束再关闭。", "提示");
                return;
            }
            base.OnFormClosing(e);
        }
    }

    class VerifyForm : Form
    {
        TextBox txtFiles, txtRepo, txtOut;
        ComboBox cboSource;
        Button btnPick, btnClear, btnGo, btnClose;
        Button btnStop;
        Label lblStatus;
        string mirror;
        List<string> picked = new List<string>();
        bool busy;
        volatile bool stopFlag;

        public VerifyForm(string mirror)
        {
            this.mirror = string.IsNullOrEmpty(mirror) ? Core.DefaultMirror : mirror;
            Text = "校验本地文件（比对站点给的 sha256 / 大小）";
            Width = 770; Height = 640;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            Font = new Font("Microsoft YaHei UI", 9f);
            Core.ApplyIcon(this);
            AllowDrop = true;
            DragEnter += DragEnterHandler;
            DragDrop += DragDropHandler;

            Label l1 = new Label();
            l1.Text = "要校验的文件（把文件从资源管理器拖进窗口，或点「选择文件」）";
            l1.SetBounds(16, 10, 620, 20);

            txtFiles = new TextBox();
            txtFiles.SetBounds(16, 34, 726, 112);
            txtFiles.Multiline = true; txtFiles.ScrollBars = ScrollBars.Vertical; txtFiles.ReadOnly = true;
            txtFiles.Font = new Font("Consolas", 9f);
            txtFiles.AllowDrop = true;
            txtFiles.DragEnter += DragEnterHandler;
            txtFiles.DragDrop += DragDropHandler;

            btnPick = new Button(); btnPick.Text = "选择文件…"; btnPick.SetBounds(16, 152, 110, 28);
            btnPick.Click += delegate
            {
                using (OpenFileDialog ofd = new OpenFileDialog())
                {
                    ofd.Multiselect = true;
                    ofd.Title = "选择要校验的文件";
                    ofd.Filter = "模型文件|*.gguf;*.safetensors;*.bin;*.pt;*.pth;*.ckpt|所有文件|*.*";
                    if (ofd.ShowDialog(this) == DialogResult.OK) AddFiles(ofd.FileNames);
                }
            };
            btnClear = new Button(); btnClear.Text = "清空"; btnClear.SetBounds(136, 152, 80, 28);
            btnClear.Click += delegate { picked.Clear(); RefreshFiles(); };

            Label l2 = new Label();
            l2.Text = "比对来源：填仓库名 / 版本 ID 就联网比对；留空只算大小和 sha256（不联网）";
            l2.SetBounds(16, 190, 700, 20);

            cboSource = new ComboBox();
            cboSource.SetBounds(16, 214, 150, 24);
            cboSource.DropDownStyle = ComboBoxStyle.DropDownList;
            cboSource.Items.Add("HuggingFace");
            cboSource.Items.Add("ModelScope");
            cboSource.Items.Add("Civitai");
            cboSource.SelectedIndex = 0;

            txtRepo = new TextBox(); txtRepo.SetBounds(178, 214, 564, 24);

            btnGo = new Button(); btnGo.Text = "开始校验"; btnGo.SetBounds(16, 250, 110, 30);
            btnGo.Click += delegate { Go(); };
            btnStop = new Button(); btnStop.Text = "停止"; btnStop.SetBounds(136, 250, 80, 30);
            btnStop.Enabled = false;
            btnStop.Click += delegate { stopFlag = true; Say("正在停止（等当前文件算完这一段）…"); };
            btnClose = new Button(); btnClose.Text = "关闭"; btnClose.SetBounds(642, 250, 100, 30);
            btnClose.Click += delegate
            {
                if (busy) { MessageBox.Show(this, "正在校验，等它结束再关。", "提示"); return; }
                Close();
            };

            lblStatus = new Label(); lblStatus.SetBounds(16, 288, 726, 20);
            lblStatus.Text = "就绪";
            lblStatus.ForeColor = Color.FromArgb(20, 90, 160);

            txtOut = new TextBox();
            txtOut.SetBounds(16, 312, 726, 280);
            txtOut.Multiline = true; txtOut.ReadOnly = true; txtOut.ScrollBars = ScrollBars.Vertical;
            txtOut.Font = new Font("Consolas", 9f);
            txtOut.BackColor = Color.White;
            txtOut.AllowDrop = true;
            txtOut.DragEnter += DragEnterHandler;
            txtOut.DragDrop += DragDropHandler;

            Controls.AddRange(new Control[] { l1, txtFiles, btnPick, btnClear, l2, cboSource, txtRepo, btnGo, btnStop, btnClose, lblStatus, txtOut });
            RefreshFiles();
        }

        void DragEnterHandler(object s, DragEventArgs e)
        {
            if (busy) { e.Effect = DragDropEffects.None; return; }
            e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        void DragDropHandler(object s, DragEventArgs e)
        {
            if (busy) return;
            string[] fs = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (fs != null) AddFiles(fs);
        }

        void AddFiles(string[] fs)
        {
            if (fs == null) return;
            int dirs = 0;
            foreach (string f in fs)
            {
                try
                {
                    if (Directory.Exists(f)) { dirs++; continue; }
                    if (File.Exists(f) && !picked.Contains(f)) picked.Add(f);
                }
                catch { }
            }
            RefreshFiles();
            if (dirs > 0)
                MessageBox.Show(this, "拖进来的是文件夹（" + dirs + " 个），请拖具体文件。", "提示");
        }

        void RefreshFiles()
        {
            txtFiles.Text = string.Join("\r\n", picked.ToArray());
            txtOut.Text = "";
            txtOut.Text = picked.Count == 0
                ? "提示：把文件拖进来（或点「选择文件」）。\r\n\r\n想比对是否正确，就填上它来自哪个仓库；只想算哈希就留空。"
                : "";
        }

        void Say(string s)
        {
            if (lblStatus.IsDisposed) return;
            lblStatus.Text = s;
        }

        async void Go()
        {
            if (busy) return;
            if (picked.Count == 0) { MessageBox.Show(this, "先选文件（或把文件拖进窗口）。", "提示"); return; }

            Source src = cboSource.SelectedIndex == 2 ? Source.Civitai :
                         (cboSource.SelectedIndex == 1 ? Source.MS : Source.HF);
            Source psrc; string repo; string hint;
            Core.ParseRepoInput(txtRepo.Text.Trim(), src, out psrc, out repo, out hint);
            if (psrc != src)
            {
                src = psrc;
                cboSource.SelectedIndex = psrc == Source.MS ? 1 : (psrc == Source.Civitai ? 2 : 0);
            }
            List<Core.FileEntry> list = null;

            stopFlag = false;
            busy = true; btnGo.Enabled = false; btnPick.Enabled = false; btnClear.Enabled = false; btnStop.Enabled = true;
            txtOut.Clear();
            try
            {
                if (repo.Length > 0)
                {
                    Say("正在获取仓库文件列表…（" + repo + "）");
                    string rp = repo;
                    list = await Task.Run(() => Core.FetchFiles(src, mirror, rp));
                    if (list == null || list.Count == 0) { txtOut.AppendText("仓库没有列出任何文件。\r\n"); return; }
                    txtOut.AppendText("仓库共 " + list.Count + " 个文件，开始逐个校验：\r\n\r\n");
                }
                int okc = 0, badc = 0, only = 0, nocmp = 0;
                foreach (string f in picked)
                {
                    string name = Path.GetFileName(f);
                    Say("校验 " + name + " …（大文件算 sha256 需要几十秒）");
                    Core.FileEntry match = null;
                    if (list != null)
                    {
                        foreach (Core.FileEntry e in list)
                            if (string.Equals(Path.GetFileName(e.Name), name, StringComparison.OrdinalIgnoreCase)) { match = e; break; }
                    }
                    string line;
                    if (list != null && match == null)
                    {
                        line = "[没找到] " + name + "：仓库里没有同名文件（可能改过文件名，或选错了仓库）";
                        badc++;
                    }
                    else if (list == null)
                    {
                        // 没填仓库：只算大小和哈希，不做「一致」判断
                        if (stopFlag) { txtOut.AppendText("[已停止]\r\n"); break; }
                        string sha0 = null;
                        long sz0 = -1;
                        try { sz0 = new FileInfo(f).Length; } catch { }
                        try { sha0 = Core.Sha256File(f, null); } catch (Exception ex) { line = "[读取失败] " + name + "：" + ex.Message; badc++; txtOut.AppendText(line + "\r\n"); continue; }
                        line = "[仅计算] " + name + "  " + Core.Fmt(sz0) + "  sha256=" + sha0 + (sz0 <= 0 ? "  ⚠ 0 字节，文件不完整" : "");
                        nocmp++;
                    }
                    else
                    {
                        long esz = match != null ? match.Size : -1;
                        string eh = match != null ? match.Hash : null;
                        string sha = null, err = null;
                        long localSize = -1;
                        try { localSize = new FileInfo(f).Length; } catch { }
                        bool canceled = false;
                        try
                        {
                            await Task.Run(delegate
                            {
                                err = Core.VerifyFile(f, esz, eh,
                                    delegate (int pct) { if (stopFlag) throw new OperationCanceledException(); },
                                    out sha);
                            });
                        }
                        catch (OperationCanceledException) { canceled = true; }
                        if (canceled)
                        {
                            txtOut.AppendText("[已停止] " + name + "\r\n");
                            Say("已停止校验");
                            break;
                        }
                        if (err == null)
                        {
                            string extra = Core.NormalizeHash(eh) == null
                                ? "（站点没给 sha256，只比了大小；本地 sha256=" + (sha ?? "-") + "）"
                                : "（sha256 " + sha.Substring(0, 12) + "… 与站点一致）";
                            line = "[一致] " + name + "  " + Core.Fmt(localSize) + "  " + extra;
                            okc++;
                        }
                        else if (err.StartsWith("无法比对", StringComparison.Ordinal))
                        {
                            line = "[无法校验] " + name + "：" + err + "（本地 sha256=" + (sha ?? "-") + "）";
                            nocmp++;
                        }
                        else
                        {
                            line = "[不一致] " + name + "：" + err + "（本地 sha256=" + (sha ?? "-") + "）";
                            badc++;
                        }
                    }
                    txtOut.AppendText(line + "\r\n");
                    only++;
                }
                txtOut.AppendText("\r\n—— 结束：一致 " + okc + "，不一致/没找到 " + badc +
                                  (nocmp > 0 ? "，只计算/无法比对 " + nocmp : "") + "，共 " + only + " 个 ——\r\n");
                Say("校验结束：一致 " + okc + "，不一致 " + badc);
            }
            catch (Exception ex)
            {
                if (!txtOut.IsDisposed) txtOut.AppendText("出错了：" + ex.Message + "\r\n");
                Say("校验失败：" + ex.Message);
            }
            finally
            {
                busy = false;
                if (!IsDisposed)
                {
                    btnGo.Enabled = true; btnPick.Enabled = true; btnClear.Enabled = true; btnStop.Enabled = false;
                }
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (busy)
            {
                e.Cancel = true;
                MessageBox.Show(this, "正在校验（算哈希），等它结束再关。", "提示");
                return;
            }
            base.OnFormClosing(e);
        }
    }

    class AriaInstallForm : Form
    {
        Label lbl;
        public string Error;

        public AriaInstallForm()
        {
            Text = "自动安装 aria2";
            Width = 500; Height = 150;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false; ControlBox = false;
            Font = new Font("Microsoft YaHei UI", 9f);
            Core.ApplyIcon(this);

            lbl = new Label();
            lbl.SetBounds(16, 26, 450, 44);
            lbl.Text = "正在准备…";
            Controls.Add(lbl);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Task t = Task.Run(delegate
            {
                try
                {
                    Core.InstallAria2(delegate (string s)
                    {
                        try { BeginInvoke(new Action(delegate { lbl.Text = s; })); } catch { }
                    });
                }
                catch (Exception ex) { Error = ex.Message; }
            });
            t.ContinueWith(delegate
            {
                try { BeginInvoke(new Action(delegate { Close(); })); } catch { }
            });
        }
    }

    class MainForm : Form
    {
        TextBox txtRepo, txtMirror, txtLog;
        ComboBox cboSource;
        CheckedListBox clb;
        Panel pnlBrand;
        PictureBox pictureBox1;
        Label lblStatus, lblTarget;
        Button btnList, btnStart, btnAll, btnNone, btnCancel;
        CheckBox chkAuto;
        List<Core.FileEntry> files = new List<Core.FileEntry>();
        volatile bool cancelFlag;
        bool busy;
        bool ignoreChecks;
        string modelsDir = Core.DefaultModelsDir;

        Source Src
        {
            get
            {
                int i = cboSource.SelectedIndex;
                if (i == 2) return Source.Civitai;
                if (i == 1) return Source.MS;
                return Source.HF;
            }
        }

        public MainForm()
        {
            Text = "大圣Ai-LocModel模型下载器（支持HuggingFace、魔塔、C站）";
            Width = 780; Height = 640;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f);
            Core.ApplyIcon(this);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            modelsDir = Core.DetectModelsDir();

            Label l1 = new Label(); l1.Text = "模型仓库（owner/仓库名，也可粘贴链接）"; l1.SetBounds(16, 12, 520, 20);
            txtRepo = new TextBox(); txtRepo.SetBounds(16, 34, 620, 24);

            Label l2 = new Label(); l2.Text = "来源 / 镜像地址"; l2.SetBounds(16, 66, 200, 20);
            cboSource = new ComboBox(); cboSource.SetBounds(16, 88, 150, 24);
            cboSource.DropDownStyle = ComboBoxStyle.DropDownList;
            cboSource.Items.Add("HuggingFace");
            cboSource.Items.Add("ModelScope");
            cboSource.Items.Add("Civitai");
            cboSource.SelectedIndex = 0;
            txtMirror = new TextBox(); txtMirror.SetBounds(176, 88, 456, 24); txtMirror.Text = Core.DefaultMirror;
            cboSource.SelectedIndexChanged += delegate { ApplySourceMirror(); };
            ApplySourceMirror();

            btnList = new Button(); btnList.Text = "获取文件列表"; btnList.SetBounds(648, 32, 110, 26);
            btnList.Click += delegate { LoadList(); };

            Button btnToken = new Button(); btnToken.Text = "Token 设置"; btnToken.SetBounds(648, 60, 110, 26);
            btnToken.Click += delegate
            {
                TokenForm tf = new TokenForm();
                if (tf.ShowDialog(this) == DialogResult.OK) lblStatus.Text = "令牌已保存（本机加密）";
            };

            Button btnRepoView = new Button(); btnRepoView.Text = "查看仓库"; btnRepoView.SetBounds(648, 88, 110, 26);            btnRepoView.Click += delegate
            {
                string repo = Core.NormRepo(txtRepo.Text);
                if (repo.Length < 3) { MessageBox.Show(this, "先填仓库名。", "提示"); return; }
                try
                {
                    if (Src == Source.Civitai) Process.Start("https://civitai.com/");
                    else if (Src == Source.MS) Process.Start(Core.MsBase + "/models/" + repo);
                    else Process.Start(txtMirror.Text.TrimEnd('/') + "/" + repo);
                }
                catch { }
            };

            btnDir = new Button(); btnDir.Text = "模型目录"; btnDir.SetBounds(648, 116, 110, 26);
            btnDir.Click += delegate
            {
                using (DirForm df = new DirForm(modelsDir))
                {
                    if (df.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(df.NewDir))
                    {
                        modelsDir = df.NewDir;
                        Core.CacheModelsDir(modelsDir);
                        lblTarget.Text = "保存到：" + modelsDir;
                        lblStatus.Text = "模型目录已更新";
                    }
                }
            };

            btnQueue = new Button(); btnQueue.Text = "批量队列"; btnQueue.SetBounds(648, 144, 110, 26);
            btnQueue.Click += delegate
            {
                // 预填也要按链接识别来源，否则粘魔塔/C站链接会把任务错标成 HF
                Source psrc; string prepo; string phint;
                Core.ParseRepoInput(txtRepo.Text.Trim(), Src, out psrc, out prepo, out phint);

                StringBuilder pf = new StringBuilder();
                if (psrc == Source.Civitai)
                {
                    pf.AppendLine("cv:" + prepo);
                }
                else
                {
                    string srcPrefix = psrc == Source.MS ? "ms:" : "";
                    foreach (object o in clb.CheckedItems)
                    {
                        Core.FileEntry fe = o as Core.FileEntry;
                        if (fe == null) continue;
                        string repo = string.IsNullOrEmpty(fe.Repo) ? prepo : fe.Repo;
                        pf.AppendLine(srcPrefix + repo + " :: " + fe.Name);
                    }
                }
                QueueForm qf = new QueueForm(modelsDir, EffectiveHfMirror, pf.ToString());
                qf.ShowDialog(this);
            };

            btnVerify = new Button(); btnVerify.Text = "校验文件"; btnVerify.SetBounds(648, 172, 110, 26);
            btnVerify.Click += delegate
            {
                using (VerifyForm vf = new VerifyForm(EffectiveHfMirror)) vf.ShowDialog(this);
            };

            Label l3 = new Label(); l3.Text = "勾选要下载的文件（.gguf 已排在前面）"; l3.SetBounds(16, 120, 400, 20);            clb = new CheckedListBox(); clb.SetBounds(16, 142, 624, 210);

            pnlBrand = new Panel();
            pnlBrand.SetBounds(16, 142, 624, 210);
            pnlBrand.BackColor = Color.FromArgb(248, 250, 253);
            pnlBrand.BorderStyle = BorderStyle.FixedSingle;

            Label lblBrand = new Label();
            lblBrand.SetBounds(10, 6, 404, 126);
            lblBrand.Font = new Font("Microsoft YaHei UI", 8f);
            lblBrand.ForeColor = Color.FromArgb(70, 82, 100);
            lblBrand.Text = "Ai工具和资料学习分享：\r\n" +
                "油管频道：https://youtu.be/p0uEXwDDAeI\r\n" +
                "B站：https://space.bilibili.com/2083795366\r\n" +
                "电报Telegram：https://t.me/xiaozcomfyui\r\n" +
                "X推特：https://x.com/dashengai_comfy\r\n" +
                "微信公众号：大时代Ai\r\n" +
                "大圣Ai自习室QQ学习群：1051067251\r\n" +
                "商务微信：qrock168";

            Label lblBrandName = new Label();
            lblBrandName.SetBounds(12, 136, 400, 40);
            lblBrandName.Font = new Font("Microsoft YaHei UI", 18f, FontStyle.Bold);
            lblBrandName.ForeColor = Color.FromArgb(200, 20, 20);
            lblBrandName.Text = "大圣Ai自习室";
            lblBrandName.TextAlign = ContentAlignment.MiddleLeft;

            pictureBox1 = new PictureBox();
            pictureBox1.SetBounds(432, 20, 160, 160);
            pictureBox1.SizeMode = PictureBoxSizeMode.Zoom;
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream st = asm.GetManifestResourceStream("brand_qr.png"))
                {
                    if (st != null) pictureBox1.Image = Image.FromStream(st);
                }
            }
            catch { }

            pnlBrand.Controls.Add(lblBrand);
            pnlBrand.Controls.Add(lblBrandName);
            pnlBrand.Controls.Add(pictureBox1);
            clb.CheckOnClick = true;
            clb.HorizontalScrollbar = true;

            chkAuto = new CheckBox();
            chkAuto.Text = "自动带上配套文件";
            chkAuto.SetBounds(240, 358, 190, 24);
            chkAuto.Checked = true;

            clb.ItemCheck += delegate (object s, ItemCheckEventArgs e)
            {
                if (!chkAuto.Checked || ignoreChecks) return;
                if (e.NewValue != CheckState.Checked) return;
                Core.FileEntry fe = clb.Items[e.Index] as Core.FileEntry;
                if (fe == null) return;
                BeginInvoke(new Action(delegate { AutoAdd(fe); }));
            };

            btnAll = new Button(); btnAll.Text = "全选"; btnAll.SetBounds(16, 358, 70, 26);
            btnAll.Click += delegate { SetAll(true); };
            btnNone = new Button(); btnNone.Text = "清空"; btnNone.SetBounds(92, 358, 70, 26);
            btnNone.Click += delegate { SetAll(false); };
            btnStart = new Button(); btnStart.Text = "开始下载"; btnStart.SetBounds(600, 356, 90, 28);
            btnStart.Click += delegate { StartDownload(); };
            btnCancel = new Button(); btnCancel.Text = "取消"; btnCancel.SetBounds(696, 356, 62, 28);
            btnCancel.Enabled = false;
            btnCancel.Click += delegate { cancelFlag = true; lblStatus.Text = "正在取消…"; };

            btnRecipe = new Button(); btnRecipe.Text = "图片/视频配方下载"; btnRecipe.SetBounds(176, 390, 152, 26);
            btnRecipe.Click += delegate { OpenRecipe(null); };

            btnTakeover = new Button(); btnTakeover.Text = "接管正在下载的"; btnTakeover.SetBounds(16, 390, 152, 26);
            btnTakeover.Click += delegate { Takeover(); };

            lblTarget = new Label(); lblTarget.SetBounds(334, 394, 404, 20);
            lblTarget.Text = "保存到：" + modelsDir;

            lblStatus = new Label(); lblStatus.SetBounds(16, 422, 742, 20);
            lblStatus.Text = "就绪";
            lblStatus.ForeColor = Color.FromArgb(20, 90, 160);

            txtLog = new TextBox(); txtLog.SetBounds(16, 446, 742, 148);
            txtLog.Multiline = true; txtLog.ReadOnly = true; txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.BackColor = Color.White; txtLog.Font = new Font("Consolas", 8.5f);
            txtLog.Text =
                "使用方法（全部走 aria2 多线程，支持断点续传）：" + Environment.NewLine +
                "1. HuggingFace：填 owner/仓库名，或直接粘贴仓库页 / 文件页链接（粘链接会自动切换来源）。" + Environment.NewLine +
                "2. 魔塔 ModelScope：同上，owner/仓库名或链接都行。" + Environment.NewLine +
                "3. C站 Civitai：填版本 ID 或模型页链接；只填文件名不行，需能访问 civitai.com。" + Environment.NewLine +
                "注：三站都只认仓库/版本，不认「文件名」；粘文件页链接会自动勾中那个文件。" + Environment.NewLine +
                "图片/视频模型请用「图片/视频配方下载」；多任务用「批量队列」；校验已下载的文件用「校验文件」。";

            Controls.AddRange(new Control[] {
                l1, txtRepo, l2, cboSource, txtMirror, btnList, btnToken, btnRepoView, btnDir, btnQueue, btnVerify, l3, clb, pnlBrand,
                btnAll, btnNone, chkAuto, btnStart, btnCancel, btnTakeover, btnRecipe, lblTarget, lblStatus, txtLog
            });

            clb.Visible = false;
            pnlBrand.Visible = true;

            if (Core.FindAria() == null)
            {
                lblStatus.Text = "未找到 aria2c.exe：请把 aria2c.exe 放到本工具同一目录，或加入系统 PATH";
                lblStatus.ForeColor = Color.Firebrick;
            }
            else if (!Core.HasSettingsModelsDir() && !Directory.Exists(modelsDir))
            {
                lblStatus.Text = "正在后台查找 LocModel 模型目录…";
                Task.Run(() => Core.ScanForModels(15)).ContinueWith(delegate (Task<string> t)
                {
                    string found = t.Result;
                    if (string.IsNullOrEmpty(found)) return;
                    try
                    {
                        BeginInvoke(new Action(delegate
                        {
                            if (Directory.Exists(modelsDir)) return;
                            modelsDir = found;
                            lblTarget.Text = "保存到：" + modelsDir;
                            lblStatus.Text = "已自动找到模型目录：" + modelsDir;
                        }));
                    }
                    catch { }
                });
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (Program.ShotMode) return;   // 截图模式：不做清理、不弹窗、不动用户文件

            EnsureAria2();   // 没有 aria2 时问一次要不要自动安装

            int stale = Core.CleanStaleParts(modelsDir, LogUi);
            if (stale > 0) LogUi("=== 已清理 " + stale + " 个残留 .part（对应文件已完整）");

            List<Core.RecipeIssue> issues = Core.RecipeIssuesDetailed(modelsDir);
            bool misplaced = false;
            foreach (Core.RecipeIssue s in issues) if (s.Message.IndexOf("位置不对", StringComparison.Ordinal) >= 0) misplaced = true;
            if (misplaced)
            {
                if (MessageBox.Show(this, "检测到配套文件放错位置（LocModel 要求放在 image\\common）。\r\n（请先退出 LocModel 再整理，避免文件被占用）\r\n\r\n要自动移动到正确位置吗？",
                    "整理配套文件", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    int moved = Core.FixMisplaced(modelsDir, LogUi);
                    MessageBox.Show(this, "已移动 " + moved + " 个文件。\r\n请重启 LocModel 后查看。", "完成");
                    issues = Core.RecipeIssuesDetailed(modelsDir);
                }
            }

            if (issues.Count > 0)
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("检测到图片/视频模型不完整（或未下完）：");
                sb.AppendLine();
                foreach (Core.RecipeIssue s in issues) sb.AppendLine("- " + s.Message);
                sb.AppendLine();
                sb.Append("要打开「图片/视频配方下载」补齐吗？\r\n（已存在的文件会自动跳过；未下完的 .part 会被重新下载）\r\n（下载前请先在 LocModel 里暂停同名任务）");
                if (MessageBox.Show(this, sb.ToString(), "模型不完整", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                {
                    OpenRecipe(issues[0].Id);
                }
            }

            List<Core.JobItem> q = Core.LoadQueue();
            int left = 0;
            foreach (Core.JobItem j in q) if (j.Status != "done" && j.Status != "cancelled") left++;
            if (left == 0) return;
            if (MessageBox.Show(this, "上次还有 " + left + " 个任务没下完（断点已保存）。\r\n要打开队列继续吗？", "继续上次下载",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                QueueForm qf = new QueueForm(modelsDir, EffectiveHfMirror, "");
                qf.ShowDialog(this);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (busy)
            {
                if (MessageBox.Show(this, "正在下载。退出会中断下载（.aria2 断点会保留，下次可续传）。\r\n确定退出吗？",
                    "确认退出", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                cancelFlag = true;
                // 给下载线程一点时间把 aria2 子进程杀掉，避免它变成孤儿进程继续写文件
                // 取消路径内部还有 WaitForExit(3000)，这里多等一点
                for (int i = 0; i < 40 && busy; i++)
                {
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(100);
                }
            }
            base.OnFormClosing(e);
        }

        Button btnRecipe;
        Button btnTakeover;
        Button btnDir;
        Button btnQueue;
        Button btnVerify;

        string hfMirror = Core.DefaultMirror;

        string EffectiveHfMirror
        {
            get
            {
                if (Src == Source.HF) return txtMirror.Text.Trim();
                return string.IsNullOrEmpty(hfMirror) ? Core.DefaultMirror : hfMirror;
            }
        }

        public void SelectSource(int index)
        {
            if (index >= 0 && index < cboSource.Items.Count) cboSource.SelectedIndex = index;
        }

        void ApplySourceMirror()
        {
            if (!txtMirror.ReadOnly) hfMirror = txtMirror.Text.Trim();
            if (Src == Source.HF)
            {
                txtMirror.ReadOnly = false;
                txtMirror.BackColor = Color.White;
                txtMirror.Text = string.IsNullOrEmpty(hfMirror) ? Core.DefaultMirror : hfMirror;
            }
            else if (Src == Source.MS)
            {
                txtMirror.ReadOnly = true;
                txtMirror.BackColor = Color.FromArgb(242, 242, 242);
                txtMirror.Text = Core.MsBase + "（魔塔固定地址）";
            }
            else
            {
                txtMirror.ReadOnly = true;
                txtMirror.BackColor = Color.FromArgb(242, 242, 242);
                txtMirror.Text = "https://civitai.com（C站固定地址）";
            }
        }

        void SetAll(bool v)
        {
            ignoreChecks = true;
            try { for (int i = 0; i < clb.Items.Count; i++) clb.SetItemChecked(i, v); }
            finally { ignoreChecks = false; }
        }

        void AutoAdd(Core.FileEntry main)
        {
            ignoreChecks = true;
            try
            {
                List<Core.FileEntry> extra = Core.PlanSet(files, main);
                int added = 0;
                foreach (Core.FileEntry f in extra)
                {
                    int idx = files.IndexOf(f);
                    if (idx >= 0 && !clb.GetItemChecked(idx)) { clb.SetItemChecked(idx, true); added++; }
                }
                if (added > 0) lblStatus.Text = "已自动勾选配套文件 " + added + " 个（mmproj / 分片）";
            }
            finally { ignoreChecks = false; }
        }

        void Append(string line)
        {
            line = Core.Redact(line);
            if (txtLog.Lines.Length > 600)
            {
                string[] keep = new string[300];
                Array.Copy(txtLog.Lines, txtLog.Lines.Length - 300, keep, 0, 300);
                txtLog.Lines = keep;
            }
            txtLog.AppendText(line + "\r\n");
            Regex r = new Regex(@"\[#\w+\s+([^\]]+)\]");
            Match m = r.Match(line);
            if (m.Success) lblStatus.Text = "下载中　" + m.Groups[1].Value;
        }

        void LogUi(string s)
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired) { try { BeginInvoke(new Action<string>(LogUi), s); } catch { } return; }
            Append(s);
        }

        // 下载期间统一锁住会互相干扰的按钮（队列/校验/目录/配方/接管）
        // 下载期统一锁按钮的辅助方法之后放 aria2 检查
        bool EnsureAria2()
        {
            if (Core.FindAria() != null) return true;

            DialogResult r = MessageBox.Show(this,
                "没有找到下载引擎 aria2c.exe。\r\n\r\n" +
                "要现在自动下载安装吗？约 2.4 MB，来自 aria2 官方 GitHub 发布包，装好后会校验 SHA256。\r\n\r\n" +
                "点「否」跳过：你也可以自己把 aria2c.exe 放到程序目录（或系统 PATH）后重启本工具。",
                "缺少下载引擎", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

            if (r != DialogResult.Yes)
            {
                if (r == DialogResult.No)
                    lblStatus.Text = "未安装 aria2：把 aria2c.exe 放到程序目录后重启本工具";
                return false;
            }

            try
            {
                using (AriaInstallForm f = new AriaInstallForm())
                {
                    f.ShowDialog(this);
                    if (!string.IsNullOrEmpty(f.Error)) throw new Exception(f.Error);
                }
                string p = Core.FindAria();
                if (p != null)
                {
                    lblStatus.Text = "aria2 已安装：" + p;
                    return true;
                }
                MessageBox.Show(this, "安装后仍然找不到 aria2c.exe，请手动把 aria2c.exe 放到程序目录。", "安装失败");
                return false;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "自动安装失败：" + ex.Message +
                    "\r\n\r\n可以手动下载：\r\n" + Core.Aria2Url + "\r\n解压后把 aria2c.exe 放到程序目录。",
                    "安装失败");
                return false;
            }
        }

        void SetBusyUi(bool b)
        {
            busy = b;
            btnStart.Enabled = !b;
            btnCancel.Enabled = b;
            btnList.Enabled = !b;
            btnQueue.Enabled = !b;
            btnVerify.Enabled = !b;
            btnDir.Enabled = !b;
            btnRecipe.Enabled = !b;
            btnTakeover.Enabled = !b;
        }

        async void Takeover()
        {
            if (busy) return;
            if (!EnsureAria2()) return;
            btnTakeover.Enabled = false;
            List<Core.Partial> ps = new List<Core.Partial>();
            try { ps = await Task.Run(() => Core.ScanPartials(modelsDir)); }
            catch (Exception ex) { MessageBox.Show(this, "扫描失败：" + ex.Message, "错误"); btnTakeover.Enabled = true; return; }

            if (ps.Count == 0)
            {
                MessageBox.Show(this, "没发现未完成的下载（.part）。\r\n先在 LocModel 里点下载，再回来点这里。", "提示");
                btnTakeover.Enabled = true;
                return;
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("检测到 " + ps.Count + " 个未完成的下载：");
            sb.AppendLine();
            foreach (Core.Partial p in ps) sb.AppendLine("  - " + p.FileName + "    [" + p.SourceNote + "]");
            sb.AppendLine();
            sb.Append("请先在 LocModel 里暂停这些下载，然后点确定：我会删掉 .part 并用 aria2 从镜像接管。继续吗？");
            if (MessageBox.Show(this, sb.ToString(), "接管下载", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            {
                btnTakeover.Enabled = true;
                return;
            }

            cancelFlag = false;
            SetBusyUi(true);
            txtLog.Clear();
            int ok = 0, fail = 0;
            foreach (Core.Partial p in ps)
            {
                if (cancelFlag) break;
                LogUi("=== 接管：" + p.FileName + "   <- " + p.Repo);
                string tm = EffectiveHfMirror;
                try
                {
                    string err = null;
                    int rc = await Task.Run(() =>
                    {
                        string inner;
                        int r = Core.RunAriaTo(Source.HF, tm, p.Repo, p.SubPath, p.FileName, p.Dir,
                            LogUi, delegate { return cancelFlag; }, out inner);
                        err = inner;
                        return r;
                    });
                    if (rc == 0) { ok++; LogUi("=== 完成：" + p.FileName); }
                    else if (rc == -2) { LogUi("=== 已取消"); break; }
                    else { fail++; LogUi("=== 失败：" + p.FileName + "  " + (err ?? "")); }
                }
                catch (Exception ex) { fail++; LogUi("=== 异常：" + ex.Message); }
            }
            SetBusyUi(false);
            if (!IsDisposed) MessageBox.Show(this, "接管结束：成功 " + ok + "，失败 " + fail, "完成");
        }

        void OpenRecipe(string selectId)
        {
            using (RecipeForm rf = new RecipeForm(modelsDir, EffectiveHfMirror, LogUi, selectId))
            {
                if (rf.ShowDialog(this) == DialogResult.OK && rf.ResultJob != null && rf.ResultJob.Files.Count > 0)
                    DownloadRecipeSet(rf.ResultJob, rf.ResultTitle);
            }
        }

        async void DownloadRecipeSet(Core.JobItem job, string title)
        {
            if (busy) return;
            if (!EnsureAria2()) return;

            lblStatus.Text = "正在统计配方大小…";
            long need = 0;
            string rm = EffectiveHfMirror;
            try
            {
                need = await Task.Run(() =>
                {
                    long sum = 0;
                    Dictionary<string, List<Core.FileEntry>> cache = new Dictionary<string, List<Core.FileEntry>>();
                    foreach (Core.JobFile f in job.Files)
                    {
                        // 顺手取回站点的期望大小/哈希，下载后就能校验
                        List<Core.FileEntry> lst;
                        if (!cache.TryGetValue(f.Repo, out lst))
                        {
                            try { lst = Core.FetchFiles(Source.HF, rm, f.Repo); }
                            catch { lst = new List<Core.FileEntry>(); }
                            cache[f.Repo] = lst;
                        }
                        foreach (Core.FileEntry e in lst)
                        {
                            if (string.Equals(e.Name, f.Path, StringComparison.OrdinalIgnoreCase))
                            {
                                f.Size = e.Size;
                                f.Hash = e.Hash;
                                break;
                            }
                        }
                        if (File.Exists(Path.Combine(f.Dir, f.SaveAs))) continue;
                        if (f.Size > 0) sum += f.Size;
                        else
                        {
                            long len = Core.HeadLength(Source.HF, rm, f.Repo, f.Path);
                            if (len > 0) sum += len;
                        }
                    }
                    return sum;
                });
            }
            catch { }
            long free = Core.FreeSpace(modelsDir);
            if (need > 0 && free >= 0 && free < need)
            {
                MessageBox.Show(this, "磁盘空间不足，已中止。\r\n\r\n需要：" + Core.Fmt(need) + "\r\n剩余：" + Core.Fmt(free),
                    "空间不足", MessageBoxButtons.OK, MessageBoxIcon.Error);
                lblStatus.Text = "已取消（空间不足）";
                return;
            }

            cancelFlag = false;
            SetBusyUi(true);
            txtLog.Clear();

            int ok = 0, fail = 0;
            string rm2 = EffectiveHfMirror;
            foreach (Core.JobFile f in job.Files)
            {
                if (cancelFlag) break;
                LogUi("=== [" + title + "] " + f.SaveAs + "   -> " + f.Dir);
                try
                {
                    string err = null;
                    int rc = await Task.Run(() =>
                    {
                        string inner;
                        int r = Core.RunAriaTo(Source.HF, rm2, f.Repo, f.Path, f.SaveAs, f.Dir, LogUi,
                            delegate { return cancelFlag; }, out inner, null, f.Size, f.Hash);
                        err = inner;
                        return r;
                    });
                    if (rc == 0) { ok++; LogUi("=== 完成：" + f.SaveAs); }
                    else if (rc == -2) { LogUi("=== 已取消"); break; }
                    else { fail++; LogUi("=== 失败：" + f.SaveAs + "  " + (err ?? "")); }
                }
                catch (Exception ex) { fail++; LogUi("=== 异常：" + ex.Message); }
            }

            SetBusyUi(false);
            string extra = "";
            foreach (string s in Core.RecipeIssues(modelsDir))
                if (s.StartsWith(title)) extra += "\r\n- " + s;
            lblStatus.Text = "配方下载结束：成功 " + ok + "，失败 " + fail;
            if (!IsDisposed)
                MessageBox.Show(this, "配方下载结束：成功 " + ok + "，失败 " + fail +
                    (extra.Length > 0 ? "\r\n\r\n仍不完整：" + extra : "\r\n\r\n三件套已齐全（主体在配方目录，编码器/VAE 在 image\\common）") +
                    "\r\n\r\n请重启 LocModel 后查看。", "完成");
        }

        async Task<List<Core.FileEntry>> FetchWithAuth(Source src, string mirror, string repo)
        {
            int attempt = 0;
            while (true)
            {
                try { return await Task.Run(() => Core.FetchFiles(src, mirror, repo)); }
                catch (AuthNeeded)
                {
                    attempt++;
                    if (attempt > 2) throw;   // 填了令牌还不行就停下，别无限弹窗
                    string who = src == Source.HF ? "HuggingFace" : (src == Source.MS ? "魔塔" : "C站");
                    string msg = who + " 拒绝了请求（HTTP 401/403）。\r\n\r\n" +
                                 "最常见的原因是：仓库名写错、仓库不存在。\r\n" +
                                 "其次是：私有仓库，或需要同意协议的 gated 仓库。\r\n" +
                                 "最后才是：确实需要令牌。\r\n\r\n" +
                                 "当前填的是：" + repo + "\r\n\r\n" +
                                 "要先填 " + who + " 令牌再试一次吗？\r\n" +
                                 "（不确定就点「否」，先检查仓库名拼写或换个来源）";
                    if (MessageBox.Show(this, msg, "访问被拒绝", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) throw;
                    using (TokenForm tf = new TokenForm())
                    {
                        if (tf.ShowDialog(this) != DialogResult.OK) throw;
                    }
                }
            }
        }

        async void LoadList()
        {
            string raw = txtRepo.Text.Trim();

            // 先剥掉 ms:/cv:/hf: 前缀，再判断是不是 URL
            string probe = raw;
            if (probe.StartsWith("ms:", StringComparison.OrdinalIgnoreCase)) probe = probe.Substring(3).Trim();
            else if (probe.StartsWith("cv:", StringComparison.OrdinalIgnoreCase)) probe = probe.Substring(4).Trim();
            else if (probe.StartsWith("hf:", StringComparison.OrdinalIgnoreCase)) probe = probe.Substring(3).Trim();
            bool isUrl = probe.StartsWith("http", StringComparison.OrdinalIgnoreCase);
            Source det;
            if (isUrl && !Core.TryDetectSource(probe, out det))
            {
                MessageBox.Show(this, "这个链接不是 HuggingFace / 魔塔 / C站 的地址，工具只支持这三站。\r\n\r\n" + probe, "不支持的网站");
                return;
            }

            Source src; string repo; string fileHint;
            Core.ParseRepoInput(raw, Src, out src, out repo, out fileHint);
            if (src != Src)
            {
                cboSource.SelectedIndex = src == Source.MS ? 1 : (src == Source.Civitai ? 2 : 0);
                lblStatus.Text = "已按链接自动切换来源：" +
                    (src == Source.MS ? "魔塔 ModelScope" : (src == Source.Civitai ? "C站 Civitai" : "HuggingFace"));
            }

            if (!isUrl && Core.LooksLikeModelFile(probe))
            {
                string tip;
                if (src == Source.Civitai)
                    tip = "这是模型文件名，不是 C站 的版本 ID。\r\n\r\n" +
                          "C站要填：模型版本 ID（纯数字）或模型页链接（civitai.com/models/数字/...）。\r\n" +
                          "找法：打开模型页 → 点进具体版本 → 看地址栏里 modelVersionId= 后面的数字。";
                else
                    tip = "「" + raw + "」看起来是模型文件名（或网页路径），不是仓库名。\r\n\r\n" +
                          "HuggingFace / 魔塔 要填 owner/仓库名，例如：\r\n" +
                          "  mradermacher/Model-NAME-GGUF\r\n" +
                          "  Qwen/Qwen2.5-7B-Instruct-GGUF\r\n\r\n" +
                          "如果你只有文件名：先去站点找到它所在的仓库，再复制仓库地址；\r\n" +
                          "如果这个文件来自 C站，把来源切到 Civitai，填模型版本 ID 或模型页链接。";
                MessageBox.Show(this, tip, "这不是仓库名");
                return;
            }
            if (src == Source.Civitai)
            {
                if (repo.Length < 2) { MessageBox.Show(this, "Civitai 请填模型版本 ID 或模型页面链接。", "提示"); return; }
            }
            else if (repo.Length < 3 || repo.IndexOf('/') < 1)
            {
                MessageBox.Show(this, "请填写 owner/仓库名，例如：mradermacher/Model-NAME-GGUF", "提示");
                return;
            }
            btnList.Enabled = false;
            lblStatus.Text = "正在获取文件列表…";
            string mirror = EffectiveHfMirror;
            try
            {
                List<Core.FileEntry> list = await FetchWithAuth(src, mirror, repo);
                files = list;
                ignoreChecks = true;
                clb.Items.Clear();
                foreach (Core.FileEntry f in list) clb.Items.Add(f);
                ignoreChecks = false;
                clb.Visible = list.Count > 0;
                pnlBrand.Visible = list.Count == 0;
                string key = (list.Count > 0 && !string.IsNullOrEmpty(list[0].Repo)) ? list[0].Repo : repo;
                lblTarget.Text = "保存到：" + Path.Combine(modelsDir, Core.FolderFor(key));
                lblStatus.Text = "共 " + list.Count + " 个文件";
                if (list.Count == 0) MessageBox.Show(this, "仓库里没有文件，或仓库名不对。", "提示");

                // 如果粘的是「文件页」链接，自动勾中那个文件
                string wantFile = fileHint;
                if (!string.IsNullOrEmpty(wantFile) && list.Count > 0)
                {
                    bool hit = false;
                    for (int i = 0; i < clb.Items.Count; i++)
                    {
                        Core.FileEntry fe = clb.Items[i] as Core.FileEntry;
                        if (fe == null) continue;
                        if (string.Equals(fe.Name, wantFile, StringComparison.OrdinalIgnoreCase) ||
                            fe.Name.EndsWith("/" + wantFile, StringComparison.OrdinalIgnoreCase))
                        {
                            clb.SetItemChecked(i, true);
                            hit = true;
                            lblStatus.Text = "已自动勾选：" + fe.Name;
                            break;
                        }
                    }
                    if (!hit) lblStatus.Text = "共 " + list.Count + " 个文件（链接里的文件名没在列表中找到）";
                }
            }
            catch (Exception ex)
            {
                ignoreChecks = false;
                lblStatus.Text = "获取失败";
                MessageBox.Show(this, "获取文件列表失败：\r\n" + ex.Message, "错误");
            }
            finally { btnList.Enabled = true; }
        }

        async void StartDownload()
        {
            if (busy) return;
            if (!EnsureAria2()) return;
            if (clb.CheckedItems.Count == 0) { MessageBox.Show(this, "请先勾选要下载的文件。", "提示"); return; }

            Source src = Src;
            string repo = (src == Source.Civitai) ? txtRepo.Text.Trim() : Core.NormRepo(txtRepo.Text);
            string mirror = EffectiveHfMirror;
            List<Core.FileEntry> chosen = new List<Core.FileEntry>();
            foreach (object o in clb.CheckedItems) chosen.Add((Core.FileEntry)o);

            long total = 0;
            foreach (Core.FileEntry f in chosen) total += f.Size;

            btnStart.Enabled = false;
            lblStatus.Text = "下载前检查中…";
            List<string> warns = new List<string>();
            try { warns = await Task.Run(() => Core.Preflight(src, mirror, repo, files, chosen, true)); }
            catch (Exception ex) { warns.Add("检查过程出错：" + ex.Message); }
            btnStart.Enabled = true;

            if (warns.Count > 0)
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("下载前检查发现问题：");
                sb.AppendLine();
                foreach (string w in warns) sb.AppendLine("- " + w);
                sb.AppendLine();
                sb.Append("仍要继续下载吗？");
                if (MessageBox.Show(this, sb.ToString(), "下载前检查", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                {
                    lblStatus.Text = "已取消（下载前检查未通过）";
                    return;
                }
            }

            long freeSpace = Core.FreeSpace(modelsDir);
            if (freeSpace >= 0 && freeSpace < total)
            {
                MessageBox.Show(this, "磁盘空间不足，已中止。\r\n\r\n需要：" + Core.Fmt(total) + "\r\n剩余：" + Core.Fmt(freeSpace),
                    "空间不足", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (MessageBox.Show(this, "将下载 " + chosen.Count + " 个文件，共 " + Core.Fmt(total) +
                (freeSpace >= 0 ? "\r\n磁盘剩余：" + Core.Fmt(freeSpace) : "") + "\r\n\r\n确定开始？", "确认",
                MessageBoxButtons.OKCancel) != DialogResult.OK) return;

            cancelFlag = false;
            SetBusyUi(true);
            txtLog.Clear();

            string dirKey = (chosen.Count > 0 && !string.IsNullOrEmpty(chosen[0].Repo)) ? chosen[0].Repo : repo;
            string dir = Path.Combine(modelsDir, Core.FolderFor(dirKey));
            int ok = 0, fail = 0;
            foreach (Core.FileEntry f in chosen)
            {
                if (cancelFlag) break;
                lblStatus.Text = "开始：" + f.Name;
                LogUi("=== " + f.Name + "  (" + Core.Fmt(f.Size) + ")");
                try
                {
                    string err = null;
                    int rc = await Task.Run(() =>
                    {
                        string inner;
                        string useRepo = string.IsNullOrEmpty(f.Repo) ? repo : f.Repo;
                        string useSaveAs = string.IsNullOrEmpty(f.SaveAs) ? f.Name : f.SaveAs;
                        int r = Core.RunAriaTo(src, mirror, useRepo, f.Name, useSaveAs, dir, LogUi,
                            delegate { return cancelFlag; }, out inner, f.Url, f.Size, f.Hash);
                        err = inner;
                        return r;
                    });
                    if (rc == 0) { ok++; LogUi("=== 完成：" + f.Name); }
                    else if (rc == -2) { LogUi("=== 已取消：" + f.Name); break; }
                    else { fail++; LogUi("=== 失败：" + f.Name + "  " + (err ?? "")); }
                }
                catch (Exception ex) { fail++; LogUi("=== 异常：" + ex.Message); }
            }

            SetBusyUi(false);
            lblStatus.Text = "结束：成功 " + ok + "，失败 " + fail + (cancelFlag ? "，已取消" : "");
            if (ok > 0 && !IsDisposed)
                MessageBox.Show(this, "下载完成。\r\n请重启 LocModel，模型会自动出现在列表里。\r\n\r\n保存位置：\r\n" + dir, "完成");
        }
    }

    static class Program
    {
        public static bool ShotMode = false;

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AttachConsole(int dwProcessId);

        // 这个程序是 winexe（GUI 子系统），从 cmd 启动时默认没有控制台，
        // 命令行功能会“什么都看不到”。有参数时挂到父进程控制台上。
        static void AttachParentConsole()
        {
            try
            {
                if (!AttachConsole(-1)) return;
                StreamWriter so = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
                Console.SetOut(so);
                try
                {
                    StreamWriter se = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true };
                    Console.SetError(se);
                }
                catch { }
            }
            catch { }
        }

        [STAThread]
        static int Main(string[] args)
        {
            try
            {
                if (args.Length == 0)
                {
                    bool created;
                    Mutex mtx = new Mutex(true, "LocModelDownloader_SingleInstance", out created);
                    if (!created)
                    {
                        MessageBox.Show("LocModel 下载器已经在运行了。\r\n（同一时间只允许开一个，避免两个实例写同一个文件）",
                            "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return 3;
                    }
                    try { return Run(args); }
                    finally { try { mtx.ReleaseMutex(); } catch { } mtx.Dispose(); }
                }
                AttachParentConsole();
                return Run(args);
            }
            catch (Exception ex)
            {
                try
                {
                    Core.Log("CLI异常: " + ex);
                    if (Environment.GetEnvironmentVariable("LOCMODEL_DL_DEBUG") == "1")
                    {
                        Console.Error.WriteLine("TYPE=" + ex.GetType().FullName);
                        Console.Error.WriteLine("MSG=" + ex.Message);
                        Console.Error.WriteLine("ST=" + ex.StackTrace);
                    }
                    else Console.Error.WriteLine("ERROR: " + ex.Message);
                }
                catch { }
                return 99;
            }
        }

        static void SplitSrc(string arg, out Source src, out string repo)
        {
            // 和界面一致：命令行也按链接域名自动判断来源
            string hint;
            Core.ParseRepoInput(arg, Source.HF, out src, out repo, out hint);
        }

        static void SplitSrc(string arg, Source current, out Source src, out string repo, out string fileHint)
        {
            Core.ParseRepoInput(arg, current, out src, out repo, out fileHint);
        }

        static int Run(string[] args)
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
            Secrets.Load();
            Core.LoadConfig();

            if (args.Length > 0 && args[0] == "--list")
            {
                if (args.Length < 2) { Console.WriteLine("usage: --list <repo>"); return 2; }
                Source s; string repo;
                SplitSrc(args[1], out s, out repo);
                List<Core.FileEntry> l = Core.FetchFiles(s, Core.DefaultMirror, repo);
                foreach (Core.FileEntry f in l)
                {
                    Console.WriteLine(f.Name + "\t" + f.Size + (string.IsNullOrEmpty(f.Url) ? "" : "\t" + f.Url));
                }
                return 0;
            }
            if (args.Length > 0 && args[0] == "--plan")
            {
                if (args.Length < 3) { Console.WriteLine("usage: --plan <repo> <file>"); return 2; }
                Source s; string repo;
                SplitSrc(args[1], out s, out repo);
                List<Core.FileEntry> all = Core.FetchFiles(s, Core.DefaultMirror, repo);
                Core.FileEntry main = null;
                foreach (Core.FileEntry f in all) if (f.Name == args[2]) main = f;
                if (main == null) { Console.WriteLine("not found: " + args[2]); return 2; }
                Console.WriteLine("MAIN  " + main.Name + "\t" + main.Size);
                foreach (Core.FileEntry f in Core.PlanSet(all, main)) Console.WriteLine("EXTRA " + f.Name + "\t" + f.Size);
                return 0;
            }
            if (args.Length > 0 && args[0] == "--check")
            {
                if (args.Length < 3) { Console.WriteLine("usage: --check <repo> <file> [file...]"); return 2; }
                Source s; string repo;
                SplitSrc(args[1], out s, out repo);
                List<Core.FileEntry> all = Core.FetchFiles(s, Core.DefaultMirror, repo);
                List<Core.FileEntry> sel = new List<Core.FileEntry>();
                for (int i = 2; i < args.Length; i++)
                {
                    Core.FileEntry hit = null;
                    foreach (Core.FileEntry f in all) if (f.Name == args[i]) hit = f;
                    if (hit == null) { hit = new Core.FileEntry(); hit.Name = args[i]; }
                    sel.Add(hit);
                }
                List<string> ws = Core.Preflight(s, Core.DefaultMirror, repo, all, sel, true);
                if (ws.Count == 0) Console.WriteLine("OK: 没有发现问题");
                foreach (string w in ws) Console.WriteLine("WARN: " + w);
                return ws.Count == 0 ? 0 : 1;
            }
            if (args.Length > 0 && args[0] == "--to")
            {
                if (args.Length < 4) { Console.WriteLine("usage: --to <repo> <in-repo path> <targetdir> [mirror]"); return 2; }
                Source s; string repo;
                SplitSrc(args[1], out s, out repo);
                string mir = args.Length > 4 ? args[4] : Core.DefaultMirror;
                long esz = -1; string eh = null;
                string useRepo = repo, useUrl = null;
                bool metaOk = false;
                try
                {
                    foreach (Core.FileEntry fe in Core.FetchFiles(s, mir, repo))
                    {
                        if (string.Equals(fe.Name, args[2], StringComparison.OrdinalIgnoreCase))
                        {
                            esz = fe.Size; eh = fe.Hash; metaOk = true;
                            if (!string.IsNullOrEmpty(fe.Repo)) useRepo = fe.Repo;
                            if (!string.IsNullOrEmpty(fe.Url)) useUrl = fe.Url;
                            break;
                        }
                    }
                }
                catch (Exception ex) { Console.WriteLine("元数据获取失败：" + ex.Message); }
                if (!metaOk) Console.WriteLine("警告：没拿到站点元数据，本次下载不做大小/哈希校验。");
                string err;
                int rc = Core.RunAriaTo(s, mir, useRepo, args[2], args[2], args[3],
                    delegate (string x) { Console.WriteLine(x); }, null, out err, useUrl, esz, eh);
                if (rc != 0) { Console.WriteLine("FAILED: " + err); return rc; }
                Console.WriteLine(Core.VerifyMode == "off"
                    ? "OK: 下载完成（未校验，verify=off）"
                    : (metaOk ? "OK: 下载完成且校验通过（" + Core.VerifyMode + "）" : "OK: 下载完成（无元数据，未校验）"));
                return 0;
            }
            if (args.Length > 0 && args[0] == "--recipe")
            {
                List<Core.Recipe> all = Core.LoadRecipes();
                if (args.Length < 2)
                {
                    Console.WriteLine("usage: --recipe <id> [quant] [modelsdir] [mirror]");
                    foreach (Core.Recipe r in all) Console.WriteLine("  " + r.Id + "   " + r.Title);
                    return 2;
                }
                Core.Recipe rec = null;
                foreach (Core.Recipe r in all) if (r.Id == args[1]) rec = r;
                if (rec == null) { Console.WriteLine("no such recipe: " + args[1]); return 2; }
                int qi = 0;
                if (args.Length > 2)
                {
                    int k = rec.QuantLabels.IndexOf(args[2]);
                    if (k < 0)
                    {
                        Console.WriteLine("没有这个量化：" + args[2] + "（可选：" + string.Join(" / ", rec.QuantLabels.ToArray()) + "）");
                        return 2;
                    }
                    qi = k;
                }
                string mdir = args.Length > 3 ? args[3] : Core.DetectModelsDir();
                string mir = args.Length > 4 ? args[4] : Core.DefaultMirror;
                string rdir = Core.RecipeFolder(mdir, rec.Id);
                Console.WriteLine("recipe=" + rec.Id + " quant=" + rec.QuantLabels[qi] + " dir=" + rdir);

                List<Core.Component> items = new List<Core.Component>();
                items.AddRange(rec.Quants[qi]);
                items.AddRange(rec.Fixed);
                foreach (Core.Component c in items)
                    Console.WriteLine("   " + c.SaveAs + "  <- " + c.Repo + " :: " + c.Path);
                return 0;
            }
            if (args.Length > 0 && args[0] == "--findmodels")
            {
                Console.WriteLine("配置 = " + (Core.HasSettingsModelsDir() ? "有（LocModel settings.json）" : "无"));
                Console.WriteLine("快速 = " + Core.DetectModelsDir());
                Console.WriteLine("全盘 = " + (Core.ScanForModels(20) ?? "(未找到)"));
                return 0;
            }
            if (args.Length > 0 && args[0] == "--cleanparts")
            {
                string md = args.Length > 1 ? args[1] : Core.DetectModelsDir();
                int n = Core.CleanStaleParts(md, delegate (string s) { Console.WriteLine(s); });
                Console.WriteLine("已清理 " + n + " 个残留 .part（目录：" + md + "）");
                return 0;
            }
            if (args.Length > 0 && args[0] == "--selftest")
            {
                int fails = Core.SelfTest(delegate (string s) { Console.WriteLine(s); });
                return fails == 0 ? 0 : 1;
            }
            if (args.Length > 0 && args[0] == "--issues")
            {
                string md = args.Length > 1 ? args[1] : Core.DetectModelsDir();
                List<string> iss = Core.RecipeIssues(md);
                Console.WriteLine("models=" + md + "  问题数=" + iss.Count);
                foreach (string s in iss) Console.WriteLine("WARN: " + s);
                return iss.Count == 0 ? 0 : 1;
            }
            if (args.Length > 0 && args[0] == "--queue")
            {
                List<Core.JobItem> q = Core.LoadQueue();
                Console.WriteLine("队列文件: " + Core.QueueFile());
                Console.WriteLine("任务数: " + q.Count);
                foreach (Core.JobItem j in q)
                {
                    Console.WriteLine("  [" + j.Status + "] " + j.Title + "  " + Core.Fmt(j.TotalSize));
                    foreach (Core.JobFile f in j.Files) Console.WriteLine("      " + f.SaveAs + "  -> " + f.Dir);
                }
                return 0;
            }
            if (args.Length > 0 && args[0] == "--addqueue")
            {
                if (args.Length < 2) { Console.WriteLine("usage: --addqueue <repo> [file]"); return 2; }
                Mutex guiMtx;
                if (Mutex.TryOpenExisting("LocModelDownloader_SingleInstance", out guiMtx))
                {
                    try { guiMtx.Dispose(); } catch { }
                    Console.WriteLine("GUI 正在运行：请关掉下载器（或先在它的队列窗口）再改队列，避免两边互相覆盖。");
                    return 3;
                }
                List<Core.JobItem> q = Core.LoadQueue();
                Source asrc; string arepo; string ahint;
                Core.ParseRepoInput(args[1], Source.HF, out asrc, out arepo, out ahint);
                string onlyFile = args.Length > 2 ? args[2] : (ahint ?? "");
                Core.JobItem job = Core.ResolveRepoJob(asrc, Core.DefaultMirror, arepo, onlyFile,
                    Core.DetectModelsDir());
                if (job.Files.Count == 0) { Console.WriteLine("解析失败"); return 1; }
                foreach (Core.JobItem e in q)
                {
                    if (Core.SameJob(e, job)) { Console.WriteLine("已在队列中，跳过: " + job.Title); return 0; }
                }
                q.Add(job);
                Core.SaveQueue(q);
                Console.WriteLine("已加入: " + job.Title + "  " + Core.Fmt(job.TotalSize));
                foreach (Core.JobFile f in job.Files) Console.WriteLine("   " + f.SaveAs);
                return 0;
            }
            if (args.Length > 0 && args[0] == "--verify")
            {
                if (args.Length < 2) { Console.WriteLine("usage: --verify <file> [repo]   （repo 可带 ms:/cv: 前缀）"); return 2; }
                string vfile = args[1];
                if (!File.Exists(vfile)) { Console.WriteLine("文件不存在: " + vfile); return 2; }
                long va = new FileInfo(vfile).Length;
                List<Core.FileEntry> vlist = null;
                if (args.Length > 2)
                {
                    Source vs; string vr;
                    SplitSrc(args[2], out vs, out vr);
                    if (vs == Source.Civitai) vr = Core.ResolveCivitaiVersion(vr);
                    vlist = Core.FetchFiles(vs, Core.DefaultMirror, vr);
                }
                Console.WriteLine("文件: " + vfile);
                Console.WriteLine("大小: " + va + " 字节  (" + Core.Fmt(va) + ")");
                string vsha = Core.Sha256File(vfile, null);
                Console.WriteLine("sha256: " + vsha);
                if (va <= 0) { Console.WriteLine("结果: 不一致 - 文件是 0 字节，不完整"); return 1; }
                if (vlist != null)
                {
                    string vbn = Path.GetFileName(vfile);
                    Core.FileEntry m = null;
                    int hits = 0;
                    foreach (Core.FileEntry e in vlist)
                        if (string.Equals(Path.GetFileName(e.Name), vbn, StringComparison.OrdinalIgnoreCase)) { if (m == null) m = e; hits++; }
                    if (m == null) { Console.WriteLine("结果: 仓库里没有同名文件（可能改过名或选错仓库）"); return 1; }
                    if (hits > 1) Console.WriteLine("提示: 仓库里有 " + hits + " 个同名文件，按第一个比对，结果仅供参考。");
                    string vh = Core.NormalizeHash(m.Hash);
                    bool sizeOk = m.Size <= 0 || va == m.Size;
                    bool hashOk = vh == null || string.Equals(vsha, vh, StringComparison.OrdinalIgnoreCase);
                    Console.WriteLine("站点: 大小 " + m.Size + "  " + (vh == null ? "（没给 sha256）" : "sha256 " + vh));
                    if (m.Size <= 0 && vh == null)
                    {
                        Console.WriteLine("结果: 无法比对（站点没给大小和哈希），只算了本地哈希");
                        return 3;
                    }
                    if (sizeOk && hashOk)
                    {
                        Console.WriteLine("结果: 一致" + (vh == null ? "（只验证了大小）" : ""));
                        return 0;
                    }
                    if (!sizeOk) Console.WriteLine("结果: 不一致 - 大小不符（应为 " + m.Size + "，实际 " + va + "）");
                    else Console.WriteLine("结果: 不一致 - sha256 不符");
                    return 1;
                }
                Console.WriteLine("结果: 没填仓库，只算了大小和 sha256（未与站点比对）");
                return 3;
            }
            if (args.Length > 0 && args[0] == "--installaria")
            {
                try
                {
                    string p = Core.InstallAria2(delegate (string s) { Console.WriteLine(s); });
                    Console.WriteLine("OK: " + p);
                    return 0;
                }
                catch (Exception ex) { Console.WriteLine("FAILED: " + ex.ToString()); return 1; }
            }
            if (args.Length > 0 && args[0] == "--doctor")
            {
                string aria = Core.FindAria();
                Console.WriteLine("aria2c   = " + (aria == null ? "(未找到)" : aria));
                Console.WriteLine("models   = " + Core.DetectModelsDir());
                Console.WriteLine("recipes  = " + Core.LoadRecipes().Count + " 条  (" + Core.RecipeFile() + ")");
                Console.WriteLine("mirror   = " + Core.DefaultMirror);
                Console.WriteLine("令牌     = HF:" + (Secrets.Hf.Length > 0 ? "已设置" : "空") +
                                  "  MS:" + (Secrets.Ms.Length > 0 ? "已设置" : "空") +
                                  "  Civitai:" + (Secrets.Civitai.Length > 0 ? "已设置" : "空"));
                return aria == null ? 1 : 0;
            }
            if (args.Length > 0 && args[0] == "--scan")
            {
                string md = args.Length > 1 ? args[1] : Core.DetectModelsDir();
                List<Core.Partial> ps = Core.ScanPartials(md);
                Console.WriteLine("models=" + md + "  partials=" + ps.Count);
                foreach (Core.Partial p in ps)
                    Console.WriteLine("  " + p.FileName + "\t[" + p.SourceNote + "]\t" + p.Repo + " :: " + p.SubPath + "\t-> " + p.Dir);
                return ps.Count == 0 ? 0 : 1;
            }
            if (args.Length > 0 && args[0] == "--shot")
            {
                Program.ShotMode = true;
                Application.EnableVisualStyles();
                if (args.Length > 2 && args[2] == "queue")
                {
                    QueueForm qf = new QueueForm(Core.DetectModelsDir(), Core.DefaultMirror, "");
                    qf.Show();
                    Application.DoEvents();
                    Bitmap qb = new Bitmap(qf.Width, qf.Height);
                    qf.DrawToBitmap(qb, new Rectangle(0, 0, qf.Width, qf.Height));
                    qb.Save(args.Length > 1 ? args[1] : "shot.png", System.Drawing.Imaging.ImageFormat.Png);
                    qf.Close();
                    return 0;
                }
                if (args.Length > 2 && args[2] == "dir")
                {
                    DirForm df = new DirForm(Core.DetectModelsDir());
                    df.Show();
                    Application.DoEvents();
                    Bitmap db = new Bitmap(df.Width, df.Height);
                    df.DrawToBitmap(db, new Rectangle(0, 0, df.Width, df.Height));
                    db.Save(args.Length > 1 ? args[1] : "shot.png", System.Drawing.Imaging.ImageFormat.Png);
                    df.Close();
                    return 0;
                }
                if (args.Length > 2 && args[2] == "verify")
                {
                    VerifyForm vf2 = new VerifyForm(Core.DefaultMirror);
                    vf2.Show();
                    Application.DoEvents();
                    Bitmap vb = new Bitmap(vf2.Width, vf2.Height);
                    vf2.DrawToBitmap(vb, new Rectangle(0, 0, vf2.Width, vf2.Height));
                    vb.Save(args.Length > 1 ? args[1] : "shot.png", System.Drawing.Imaging.ImageFormat.Png);
                    vf2.Close();
                    return 0;
                }
                if (args.Length > 2 && args[2] == "recipe")
                {
                    RecipeForm rf = new RecipeForm(Core.DetectModelsDir(), Core.DefaultMirror, null, null);
                    rf.Show();
                    Application.DoEvents();
                    Bitmap rb = new Bitmap(rf.Width, rf.Height);
                    rf.DrawToBitmap(rb, new Rectangle(0, 0, rf.Width, rf.Height));
                    rb.Save(args.Length > 1 ? args[1] : "shot.png", System.Drawing.Imaging.ImageFormat.Png);
                    rf.Close();
                    return 0;
                }
                if (args.Length > 2 && (args[2] == "ms" || args[2] == "cv"))
                {
                    MainForm f3 = new MainForm();
                    f3.SelectSource(args[2] == "ms" ? 1 : 2);
                    f3.Show();
                    Application.DoEvents();
                    Bitmap b3 = new Bitmap(f3.Width, f3.Height);
                    f3.DrawToBitmap(b3, new Rectangle(0, 0, f3.Width, f3.Height));
                    b3.Save(args.Length > 1 ? args[1] : "shot.png", System.Drawing.Imaging.ImageFormat.Png);
                    f3.Close();
                    return 0;
                }
                MainForm f = new MainForm();
                f.Show();
                Application.DoEvents();
                Bitmap bmp = new Bitmap(f.Width, f.Height);
                f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                bmp.Save(args.Length > 1 ? args[1] : "shot.png", System.Drawing.Imaging.ImageFormat.Png);
                f.Close();
                return 0;
            }

            if (args.Length > 0)
            {
                Console.WriteLine("未知参数：" + args[0]);
                Console.WriteLine("可用参数：");
                Console.WriteLine("  --doctor / --selftest / --issues [目录] / --cleanparts [目录] / --findmodels / --scan [目录]");
                Console.WriteLine("  --list <repo> / --plan <repo> <file> / --check <repo> <file...>");
                Console.WriteLine("  --queue / --addqueue <repo> [file] / --verify <文件> [仓库]");
                Console.WriteLine("  --to <repo> <文件> <目录> [mirror] / --recipe <id> [quant] [目录]");
                Console.WriteLine("  --shot <png> [queue|verify|recipe|dir|ms|cv]");
                Console.WriteLine("仓库可带 ms: / cv: 前缀切换来源。");
                return 2;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }
    }
}

