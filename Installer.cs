// ClaudeDesktop-Installer — 全 C# 实现（无 PowerShell 脚本依赖）
// 功能：
//   1. 多源下载 Claude Desktop MSIX（GitHub+gh-proxy 双前缀 / R2 / 官方 redirect 兜底），流式下载实时进度
//   2. SHA256 校验（按源比对 checksums）
//   3. MSIX 安装（优先 Add-AppxProvisionedPackage 机器范围，回退 Add-AppxPackage）
//   4. Node.js 检测 / npmmirror 镜像静默安装
//   5. npm 镜像 + 全局安装 @anthropic-ai/claude-code
//   6. 修复 "Host Claude Code binary not available"：复制 claude.exe + .verified
//   7. 重启并验证
// 编译：build-exe.bat（本机 .NET Framework csc + System.Management.Automation.dll，无第三方依赖）
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using System.Management.Automation;

namespace ClaudeInstaller
{
    internal static class Program
    {
        // ---- 配置 ----
        private static bool Force, SkipClaudeCode, SkipNodeJs, SkipChecksum, SkipApi;
        private static string NodeVersion = "v24.19.0";
        private static string ClaudeCodeVersion = "";
        private static string Mirror = "github";
        private static string ApiBaseUrl = "";
        private static string ApiKey = "";
        private static string ApiModels = "";
        private const string DefaultApiBaseUrl = "https://n.tokeness.io";
        // 官方最新模型（2026-08：Opus 5 / Sonnet 5 / Haiku 4.5），回车默认采用
        private const string DefaultApiModels = "claude-opus-5,claude-sonnet-5,claude-haiku-4-5";

        // 下载缓存目录（%LOCALAPPDATA%\ClaudeInstaller\cache，跨重启持久）
        private static string CacheDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeInstaller", "cache"); }
        }

        // ---- 常量 ----
        private const string MirrorRepo = "Wangnov/claude-app-mirror";
        private static readonly string[] GhProxyPrefixes = {
            "https://v4.gh-proxy.org/https://github.com",
            "https://gh-proxy.org/https://github.com"
        };
        private const string R2LatestBase = "https://claudeapp.agentsmirror.com/latest";
        private const string OfficialApiBase = "https://claude.ai/api/desktop";
        private const string NpmRegistryMirror = "https://registry.npmmirror.com";
        private const string NodeMirrorBase = "https://npmmirror.com/mirrors/node";

        // ---- Appx 信息 ----
        private class AppxInfo
        {
            public string Name, Version, PackageFamilyName, InstallLocation, PackageFullName;
        }
        private class MsixSource
        {
            public string Name, Url, SaveAs, Kind, GhPrefix;
        }

        [STAThread]
        private static int Main(string[] args)
        {
            try { Console.Title = "Claude Desktop Installer"; } catch { }

            // 参数解析
            foreach (string a in args)
            {
                string arg = a;
                string val = "";
                int eq = a.IndexOf('=');
                if (eq > 0) { arg = a.Substring(0, eq); val = a.Substring(eq + 1); }
                if (arg == "-h" || arg == "-?" || arg == "--help") { PrintUsage(); Pause(); return 0; }
                if (arg == "-Force") Force = true;
                else if (arg == "-SkipClaudeCode") SkipClaudeCode = true;
                else if (arg == "-SkipNodeJs") SkipNodeJs = true;
                else if (arg == "-SkipChecksum") SkipChecksum = true;
                else if (arg == "-SkipApi") SkipApi = true;
                else if (arg == "-ApiBaseUrl") ApiBaseUrl = (eq > 0 ? val : NextArg(args, a)).Trim();
                else if (arg == "-ApiKey") ApiKey = (eq > 0 ? val : NextArg(args, a)).Trim();
                else if (arg == "-ApiModels") ApiModels = (eq > 0 ? val : NextArg(args, a)).Trim();
                else if (arg == "-NodeVersion" || arg == "-NodeVersion=") { NodeVersion = eq > 0 ? val : NextArg(args, a); }
                else if (arg == "-ClaudeCodeVersion") { ClaudeCodeVersion = eq > 0 ? val : NextArg(args, a); }
                else if (arg == "-Mirror") { Mirror = eq > 0 ? val : NextArg(args, a); }
            }

            // 管理员检查 / 提权
            if (!IsAdministrator())
            {
                Console.WriteLine("请求管理员权限，请在 UAC 弹窗中点击“是”...");
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo();
                    psi.UseShellExecute = true;
                    psi.Verb = "runas";
                    psi.FileName = Process.GetCurrentProcess().MainModule.FileName;
                    StringBuilder sb = new StringBuilder();
                    foreach (string a in args) sb.Append(" \"").Append(a.Replace("\"", "\\\"")).Append("\"");
                    psi.Arguments = sb.ToString();
                    Process.Start(psi);
                    return 0;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("提权失败: " + ex.Message);
                    Pause();
                    return 3;
                }
            }

            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }

            try
            {
                Banner();

                // [1] 架构
                string arch = GetArch();
                Ok("系统架构: " + arch);

                // [2] 下载 MSIX
                string msix = GetClaudeMsix(arch);

                // [3] 安装 MSIX
                AppxInfo appx = InstallMsix(msix);

                // [4] 用户数据目录
                string userData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Packages", appx.PackageFamilyName, "LocalCache", "Local", "Claude-3p");
                Ok("用户数据目录: " + userData);

                // [5] Claude Code 修复
                if (!SkipClaudeCode)
                {
                    string nodeExe = FindNodeExe();
                    if (nodeExe != null)
                    {
                        // 已安装则不重复安装，仅提示版本
                        string nodeVer = GetNodeVersion(nodeExe);
                        Ok("Node.js 已安装，跳过安装: " + nodeExe + " (" + nodeVer + ")");
                        Match vm = Regex.Match(nodeVer, "(\\d+)");
                        int major = 0;
                        if (vm.Success) int.TryParse(vm.Groups[1].Value, out major);
                        if (major > 0 && major < 18)
                            Warn("Node.js 版本过低（" + nodeVer + "，需要 >= 18），claude-code 可能无法运行。请升级 Node.js 后重试。");
                    }
                    else if (SkipNodeJs)
                    {
                        Warn("系统缺少 Node.js 且已指定 -SkipNodeJs，将跳过 Claude Code 修复");
                        nodeExe = null;
                    }
                    else
                    {
                        InstallNode(NodeVersion);
                        nodeExe = FindNodeExe();
                        if (nodeExe == null) throw new Exception("Node.js 安装后仍无法定位 node.exe");
                    }

                    if (nodeExe != null)
                    {
                        // Node 可能是本脚本刚安装的：当前进程 PATH 是启动时快照，不会自动刷新，
                        // 立即把 node 目录注入进程 PATH，后续 npm / postinstall 子进程才能找到 node
                        EnsureNodeInPath(nodeExe);
                        string cli = InstallClaudeCodeCli(nodeExe);
                        string version = ClaudeCodeVersion;
                        if (string.IsNullOrEmpty(version))
                        {
                            Step("确定 claude-code 版本（默认使用 npm 最新版）");
                            version = GetNpmPackageVersion(cli);
                            version = FindRequiredVersion(userData, version);
                        }
                        if (string.IsNullOrEmpty(version))
                            version = "latest";
                        Ok("所需版本: " + version);
                        InstallClaudeCodeBinary(userData, cli, version);
                    }
                }
                else Warn("已跳过 Claude Code 修复 (-SkipClaudeCode)");

                // [6] API 中转配置（Tokeness.io 等 Anthropic 兼容端点）
                if (!SkipApi) ConfigureApi(userData);

                // [7] 创建桌面快捷方式
                CreateDesktopShortcut(appx);

                // [7] 重启应用
                RestartClaude(appx);

                // [8] 验证
                Step("验证安装结果");
                AppxInfo final = GetClaudeAppx();
                if (final != null)
                {
                    Ok("Claude Desktop: " + final.Name + " " + final.Version);
                    Ok("PackageFamilyName: " + final.PackageFamilyName);
                }
                if (!SkipClaudeCode)
                {
                    string ccDir = Path.Combine(userData, "claude-code");
                    if (Directory.Exists(ccDir))
                    {
                        string[] dirs = Directory.GetDirectories(ccDir);
                        if (dirs.Length > 0)
                        {
                            bool exeOk = File.Exists(Path.Combine(dirs[0], "claude.exe"));
                            bool vfOk = File.Exists(Path.Combine(dirs[0], ".verified"));
                            if (exeOk && vfOk) Ok("Claude Code 修复: claude.exe + .verified 已就位");
                            else Warn("Claude Code 目录存在但文件不完整 (exe=" + exeOk + ", verified=" + vfOk + ")");
                        }
                        else Warn("未找到 claude-code 版本目录，Claude Code 修复可能未生效");
                    }
                    else Warn("未找到 claude-code 目录，Claude Code 修复可能未生效");
                }

                Console.WriteLine("\n===============================================");
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine(" 安装完成。");
                Console.ResetColor();
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(" 注意: 本程序仅解决「下载安装包/组件」的网络问题。");
                Console.WriteLine(" 登录账号与日常使用仍需要可访问 Anthropic 的网络环境（代理或中转）。");
                Console.ResetColor();
                Console.WriteLine("===============================================");
            }
            catch (Exception ex)
            {
                Fail("执行失败: " + ex.Message);
                Console.WriteLine(ex.StackTrace);
                Pause();
                return 1;
            }
            Pause();
            return 0;
        }

        private static string NextArg(string[] args, string current)
        {
            for (int i = 0; i < args.Length; i++)
                if (args[i] == current && i + 1 < args.Length) return args[i + 1];
            return "";
        }

        // ================= 工具 =================
        private static void Step(string msg) { Console.WriteLine("\n==> " + msg); }
        private static void Ok(string msg) { Console.WriteLine("  [OK] " + msg); }
        private static void Warn(string msg) { Console.WriteLine("  [!!] " + msg); }
        private static void Fail(string msg) { Console.WriteLine("  [XX] " + msg); }
        private static void Banner()
        {
            Console.WriteLine("===============================================");
            Console.WriteLine(" Claude Desktop 一键安装程序（国内网络优化版）");
            Console.WriteLine("===============================================");
        }
        private static void Pause()
        {
            try
            {
                // 输入被重定向（管道/自动化）时不等待按键，避免进程挂起残留
                if (Console.IsInputRedirected) return;
                Console.WriteLine("\n按任意键退出...");
                Console.ReadKey(true);
            }
            catch { }
        }
        private static bool IsAdministrator()
        {
            try
            {
                using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        private static string GetArch()
        {
            string envArch = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE");
            if (envArch != null && envArch.IndexOf("ARM64", StringComparison.OrdinalIgnoreCase) >= 0) return "arm64";
            if (envArch != null && (envArch.IndexOf("AMD64") >= 0 || envArch.IndexOf("x86_64") >= 0)) return "x64";
            if (envArch != null && (envArch.IndexOf("x86") >= 0 || envArch.IndexOf("IA32") >= 0))
                throw new Exception("检测到 32 位系统（x86），Claude Desktop 仅支持 x64/arm64。");
            throw new Exception("无法识别架构: " + envArch);
        }

        // ================= 下载（流式 + 实时进度） =================
        private static bool DownloadFile(string url, string dest, string desc, long minBytes)
        {
            Console.WriteLine("  下载中: " + desc + " (" + url + ")");
            Stopwatch sw = Stopwatch.StartNew();
            bool ok = false;
            const int maxAttempts = 3;
            for (int attempt = 1; attempt <= maxAttempts && !ok; attempt++)
            {
                HttpWebRequest req = null; HttpWebResponse resp = null; Stream stream = null; FileStream fs = null;
                if (File.Exists(dest)) File.Delete(dest);
                try
                {
                    req = (HttpWebRequest)WebRequest.Create(url);
                    req.Method = "GET";
                    req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
                    req.Timeout = 30000;
                    req.ReadWriteTimeout = 60000;
                    req.AllowAutoRedirect = true;
                    resp = (HttpWebResponse)req.GetResponse();
                    long total = resp.ContentLength;
                    stream = resp.GetResponseStream();
                    fs = File.Create(dest);
                    byte[] buf = new byte[65536];
                    long done = 0, lastBytes = 0;
                    DateTime lastTick = DateTime.Now;
                    Stopwatch progSw = Stopwatch.StartNew();
                    int n;
                    while ((n = stream.Read(buf, 0, buf.Length)) > 0)
                    {
                        fs.Write(buf, 0, n);
                        done += n;
                        if (progSw.ElapsedMilliseconds >= 200)
                        {
                            double dt = (DateTime.Now - lastTick).TotalSeconds;
                            double speed = Math.Max(done - lastBytes, 0) / Math.Max(dt, 0.001) / 1048576.0;
                            lastBytes = done; lastTick = DateTime.Now;
                            if (total > 0)
                                Console.Write(string.Format("\r  进度 {0,3:N0}% | {1,7:N1} / {2,7:N1} MB | {3,5:N2} MB/s ", Math.Min(100, done * 100.0 / total), done / 1048576.0, total / 1048576.0, speed));
                            else
                                Console.Write(string.Format("\r  已下载 {0,7:N1} MB | {1,5:N2} MB/s ", done / 1048576.0, speed));
                            progSw.Restart();
                        }
                    }
                    progSw.Stop();
                    Console.WriteLine();
                    fs.Close(); stream.Close(); resp.Close();
                    ok = true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine();
                    Fail(string.Format("第 {0} 次下载失败: {1}", attempt, ex.Message));
                    try { if (fs != null) fs.Close(); } catch { }
                    try { if (stream != null) stream.Close(); } catch { }
                    try { if (resp != null) resp.Close(); } catch { }
                    if (attempt < maxAttempts) Thread.Sleep(2000);
                }
            }
            sw.Stop();
            if (!ok)
            {
                if (File.Exists(dest)) File.Delete(dest);
                return false;
            }
            if (!File.Exists(dest)) { Fail("下载失败: 文件不存在"); return false; }
            long size = new FileInfo(dest).Length;
            if (size < minBytes)
            {
                Fail(string.Format("下载异常: 文件过小 ({0} bytes)，可能被重定向到错误页面", size));
                File.Delete(dest);
                return false;
            }
            double avg = (size / 1048576.0) / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
            Ok(string.Format("已下载 {0} ({1:N1} MB, 平均 {2:N2} MB/s)", dest, size / 1048576.0, avg));
            return true;
        }

        private static string GetWebString(string url)
        {
            using (WebClient wc = new WebClient())
            {
                wc.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                return wc.DownloadString(url);
            }
        }

        // ================= 校验 =================
        private static string Sha256File(string path)
        {
            using (FileStream fs = File.OpenRead(path))
            using (SHA256Managed sha = new SHA256Managed())
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLower();
        }

        private static string GetGitHubLatestTag()
        {
            try
            {
                string json = GetWebString("https://api.github.com/repos/" + MirrorRepo + "/releases/latest");
                Match m = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
                return m.Success ? m.Groups[1].Value : null;
            }
            catch { return null; }
        }

        private static string GetChecksum(string kind, string fileName, string tag, string ghPrefix)
        {
            try
            {
                if (kind == "r2")
                {
                    string text = GetWebString(R2LatestBase + "/checksums");
                    foreach (string line in text.Split('\n'))
                    {
                        string[] p = line.Trim().Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (p.Length >= 2 && p[1] == fileName) return p[0].ToLower();
                    }
                }
                else if (kind == "github" && !string.IsNullOrEmpty(tag))
                {
                    string sumUrl = ghPrefix + "/" + MirrorRepo + "/releases/download/" + tag + "/SHA256SUMS.txt";
                    string tmp = Path.Combine(Path.GetTempPath(), "Claude-SHA256SUMS.txt");
                    if (DownloadFile(sumUrl, tmp, "SHA256SUMS.txt", 16))
                    {
                        foreach (string line in File.ReadAllLines(tmp))
                        {
                            string[] p = line.Trim().Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                            if (p.Length >= 2 && p[1] == fileName) { try { File.Delete(tmp); } catch { } return p[0].ToLower(); }
                        }
                        try { File.Delete(tmp); } catch { }
                    }
                }
            }
            catch { }
            return null;
        }

        // ================= MSIX 下载（多源回退） =================
        private static List<MsixSource> BuildSources(string arch, string tag)
        {
            string msixName = "Claude-win-" + arch + ".msix";
            List<MsixSource> list = new List<MsixSource>();
            if (!string.IsNullOrEmpty(tag))
                foreach (string prefix in GhProxyPrefixes)
                    list.Add(new MsixSource { Name = "GitHub+gh-proxy", Url = prefix + "/" + MirrorRepo + "/releases/download/" + tag + "/" + msixName, SaveAs = msixName, Kind = "github", GhPrefix = prefix });
            list.Add(new MsixSource { Name = "R2镜像", Url = R2LatestBase + "/win-" + arch, SaveAs = msixName, Kind = "r2", GhPrefix = "" });
            list.Add(new MsixSource { Name = "官方redirect", Url = OfficialApiBase + "/win32/" + arch + "/msix/latest/redirect", SaveAs = msixName, Kind = "official", GhPrefix = "" });
            // 按 Mirror 关键词排序
            if (Mirror == "github") list = list.OrderBy(s => s.Kind == "github" ? 0 : 1).ToList();
            else if (Mirror == "r2") list = list.OrderBy(s => s.Kind == "r2" ? 0 : 1).ToList();
            else if (Mirror == "official") list = list.OrderBy(s => s.Kind == "official" ? 0 : 1).ToList();
            return list;
        }

        private static string GetClaudeMsix(string arch)
        {
            Directory.CreateDirectory(CacheDir);
            string cacheFile = Path.Combine(CacheDir, "Claude-win-" + arch + ".msix");
            Step("获取镜像仓库最新版本信息");
            string tag = GetGitHubLatestTag();
            if (tag != null) Ok("GitHub 镜像最新 tag: " + tag); else Warn("无法获取 GitHub tag，将跳过 GitHub 源");
            List<MsixSource> sources = BuildSources(arch, tag);

            // 缓存命中：已有文件且 SHA256 与任一源期望一致 → 直接复用（版本更新时哈希变化自动失效重下）
            if (!SkipChecksum && File.Exists(cacheFile))
            {
                string cachedSha = Sha256File(cacheFile);
                foreach (MsixSource s in sources)
                {
                    string expected = GetChecksum(s.Kind, s.SaveAs, tag, s.GhPrefix);
                    if (!string.IsNullOrEmpty(expected) && cachedSha == expected)
                    {
                        Ok("缓存命中，跳过下载: " + cacheFile + " (SHA256 匹配)");
                        return cacheFile;
                    }
                }
                Warn("缓存文件校验不匹配（可能是旧版本），将重新下载");
            }

            foreach (MsixSource s in sources)
            {
                Step("尝试下载源: " + s.Name);
                string dest = Path.Combine(CacheDir, s.SaveAs);
                if (File.Exists(dest)) File.Delete(dest);
                if (!DownloadFile(s.Url, dest, "Claude Desktop MSIX (" + arch + ")", 1048576)) continue;
                if (!SkipChecksum)
                {
                    string expected = GetChecksum(s.Kind, s.SaveAs, tag, s.GhPrefix);
                    if (expected != null)
                    {
                        string actual = Sha256File(dest);
                        if (actual != expected)
                        {
                            Fail(string.Format("SHA256 校验失败: 期望 {0} / 实际 {1}", expected, actual));
                            File.Delete(dest);
                            continue;
                        }
                        Ok("SHA256 校验通过: " + actual);
                    }
                    else Warn("未获取到期望校验和（" + s.Kind + "），跳过校验");
                }
                Ok("已缓存: " + dest);
                return dest;
            }
            throw new Exception("所有下载源均失败，安装中止。可尝试: 本机挂代理后 -Mirror official，或检查网络。");
        }

        // ================= MSIX 安装（PowerShell SDK 进程内调用） =================
        private static List<AppxInfo> GetAppxPackages(string nameFilter)
        {
            List<AppxInfo> list = new List<AppxInfo>();
            using (PowerShell ps = PowerShell.Create())
            {
                ps.AddCommand("Get-AppxPackage");
                if (!string.IsNullOrEmpty(nameFilter)) ps.AddParameter("Name", nameFilter);
                foreach (PSObject r in ps.Invoke())
                {
                    AppxInfo info = new AppxInfo
                    {
                        Name = GetProp(r, "Name"),
                        Version = GetProp(r, "Version"),
                        PackageFamilyName = GetProp(r, "PackageFamilyName"),
                        InstallLocation = GetProp(r, "InstallLocation"),
                        PackageFullName = GetProp(r, "PackageFullName")
                    };
                    list.Add(info);
                }
            }
            return list;
        }

        private static AppxInfo GetClaudeAppx()
        {
            foreach (AppxInfo i in GetAppxPackages("*Claude*"))
                if (i.Name.IndexOf("ClaudeCode", StringComparison.OrdinalIgnoreCase) < 0) return i;
            return null;
        }

        private static string GetProp(PSObject obj, string name)
        {
            PSPropertyInfo pr = obj.Properties[name];
            return (pr != null && pr.Value != null) ? pr.Value.ToString() : "";
        }

        private static string CollectErrors(PowerShell ps)
        {
            StringBuilder sb = new StringBuilder();
            foreach (ErrorRecord e in ps.Streams.Error)
                sb.Append(e.ToString()).Append("; ");
            return sb.ToString();
        }

        private static bool TryProvision(string msixPath, out string err)
        {
            err = "";
            using (PowerShell ps = PowerShell.Create())
            {
                ps.AddCommand("Add-AppxProvisionedPackage")
                  .AddParameter("Online", true)
                  .AddParameter("PackagePath", msixPath)
                  .AddParameter("SkipLicense", true)
                  .AddParameter("Regions", "all");
                ps.Invoke();
                if (ps.HadErrors) { err = CollectErrors(ps); return false; }
            }
            return true;
        }

        private static bool AddAppx(string msixPath, out string err)
        {
            err = "";
            using (PowerShell ps = PowerShell.Create())
            {
                ps.AddCommand("Add-AppxPackage").AddParameter("Path", msixPath);
                ps.Invoke();
                if (ps.HadErrors) { err = CollectErrors(ps); return false; }
            }
            return true;
        }

        private static void RemoveAppx(string packageFullName)
        {
            using (PowerShell ps = PowerShell.Create())
            {
                ps.AddCommand("Remove-AppxPackage").AddParameter("Package", packageFullName);
                ps.Invoke();
            }
        }

        private static AppxInfo InstallMsix(string msixPath)
        {
            Step("安装 MSIX 包");
            AppxInfo existing = GetClaudeAppx();
            if (existing != null && !Force)
            {
                Ok("已检测到 Claude Desktop（版本 " + existing.Version + "），跳过安装（使用 -Force 可重装）");
                return existing;
            }
            if (existing != null && Force)
            {
                Warn("卸载旧版本 " + existing.Version + " ...");
                RemoveAppx(existing.PackageFullName);
            }
            string err;
            if (TryProvision(msixPath, out err))
            {
                Ok("机器范围注册成功（所有用户可用，含 Cowork）");
                // 机器范围注册不会立即注册给当前用户（用户登录/重新登录后系统自动部署），
                // 因此补一次用户级注册，让当前会话立即可用；失败不阻断（登录后仍会部署）
                try
                {
                    if (!AddAppx(msixPath, out err))
                        Warn("用户级立即注册失败（重新登录后系统会自动部署，可忽略）: " + err);
                    else Ok("用户级立即注册成功（当前会话可用）");
                }
                catch (Exception ex) { Warn("用户级立即注册异常（可忽略）: " + ex.Message); }
            }
            else
            {
                Warn("机器范围注册失败（" + (err.Length > 0 ? err : "未知错误") + "），回退用户级安装 ...");
                if (!AddAppx(msixPath, out err)) throw new Exception("Add-AppxPackage 失败: " + err);
                Ok("用户级安装成功");
            }
            AppxInfo a = GetClaudeAppx();
            if (a == null) a = GetProvisionedAppx();   // 机器范围包信息兜底（DISM 查询）
            if (a == null)
                throw new Exception("安装已完成，但未能检测到 Claude Desktop 包（可能需重新登录后生效）。请重新登录后检查。");
            return a;
        }

        // 从机器范围注册（provisioned）的包中查找 Claude（当前用户未注册时兜底）
        private static AppxInfo GetProvisionedAppx()
        {
            try
            {
                using (PowerShell ps = PowerShell.Create())
                {
                    ps.AddCommand("Get-AppxProvisionedPackage").AddParameter("Online", true);
                    foreach (PSObject r in ps.Invoke())
                    {
                        string pn = GetProp(r, "PackageName");
                        if (!string.IsNullOrEmpty(pn) && pn.IndexOf("Claude", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return new AppxInfo
                            {
                                Name = "Claude",
                                Version = ExtractVersionFromPackageName(pn),
                                PackageFamilyName = GetProp(r, "PackageFamilyName"),
                                PackageFullName = pn,
                                InstallLocation = GetProp(r, "InstallLocation")
                            };
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        // PackageName 形如 Claude_1.24012.9.0_x64__pzs8sxrjxfjjc
        private static string ExtractVersionFromPackageName(string packageName)
        {
            Match m = Regex.Match(packageName, "_((\\d+\\.){3}\\d+)_");
            return m.Success ? m.Groups[1].Value : "";
        }

        // ================= Node.js =================
        // 把 node 所在目录注入当前进程 PATH（幂等）。Node 可能是本脚本刚装的，
        // 进程 PATH 是启动时快照不会自动刷新，注入后子进程（cmd/npm/postinstall）才能找到 node
        private static void EnsureNodeInPath(string nodeExe)
        {
            try
            {
                string nodeDir = Path.GetDirectoryName(nodeExe);
                if (string.IsNullOrEmpty(nodeDir)) return;
                string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
                if (pathEnv.IndexOf(nodeDir, StringComparison.OrdinalIgnoreCase) < 0)
                    Environment.SetEnvironmentVariable("PATH", nodeDir + ";" + pathEnv);
            }
            catch { }
        }

        private static string FindNodeExe()
        {
            string programData = Environment.GetEnvironmentVariable("ProgramData");
            if (string.IsNullOrEmpty(programData)) programData = @"C:\ProgramData";
            // 1) 常见固定安装路径（MSI / scoop / chocolatey）
            string[] fixedPaths = {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs", "node.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "shims", "node.exe"),
                Path.Combine(programData, "chocolatey", "bin", "node.exe")
            };
            foreach (string c in fixedPaths)
                if (!string.IsNullOrEmpty(c) && File.Exists(c)) return c;
            // 2) 注册表 App Paths（MSI 安装会注册，任意盘符都能命中）
            try
            {
                using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\node.exe"))
                {
                    if (key != null)
                    {
                        string v = key.GetValue("") as string;
                        if (!string.IsNullOrEmpty(v) && File.Exists(v)) return v;
                    }
                }
            }
            catch { }
            // 3) where.exe 标准查询（PATH + 当前目录）
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("where.exe", "node");
                psi.UseShellExecute = false; psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
                Process p = Process.Start(psi);
                string outStr = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode == 0)
                    foreach (string line in outStr.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string t = line.Trim();
                        if (File.Exists(t)) return t;
                    }
            }
            catch { }
            // 4) 便携目录（用户目录 / LocalAppData，nvm 等场景）
            string[] portable = {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "nodejs", "node.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "nodejs", "node.exe")
            };
            foreach (string c in portable) if (File.Exists(c)) return c;
            // 5) PATH 全量搜索（自定义任意目录）
            string path = Environment.GetEnvironmentVariable("PATH");
            if (path != null)
                foreach (string dir in path.Split(';'))
                {
                    string d = dir.Trim().Trim('"');
                    if (string.IsNullOrEmpty(d) || !Directory.Exists(d)) continue;
                    string cand = Path.Combine(d, "node.exe");
                    if (File.Exists(cand)) return cand;
                }
            return null;
        }

        private static string GetNodeVersion(string nodeExe)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(nodeExe, "--version");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                Process p = Process.Start(psi);
                string v = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                return v.Trim();
            }
            catch { return ""; }
        }

        private static bool InstallNode(string version)
        {
            Step("安装 Node.js " + version + "（npmmirror 镜像）");
            string url = NodeMirrorBase + "/" + version + "/node-" + version + "-x64.msi";
            Directory.CreateDirectory(CacheDir);
            string msi = Path.Combine(CacheDir, "node-" + version + "-x64.msi");
            // 缓存命中：安装包已存在且体积合理 → 跳过下载
            if (File.Exists(msi) && new FileInfo(msi).Length >= 10 * 1024 * 1024)
            {
                Ok("Node.js 安装包已缓存，跳过下载: " + msi);
            }
            else
            {
                if (File.Exists(msi)) File.Delete(msi);
                if (!DownloadFile(url, msi, "Node.js " + version + " MSI", 1048576)) return false;
                Ok("已缓存: " + msi);
            }
            Console.WriteLine("  静默安装中（msiexec /qn）...");
            ProcessStartInfo psi = new ProcessStartInfo("msiexec.exe", "/i \"" + msi + "\" /qn /norestart");
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            Process p = Process.Start(psi);
            p.WaitForExit();
            if (p.ExitCode == 0 || p.ExitCode == 3010)
            {
                Ok("Node.js 安装完成 (exit=" + p.ExitCode + ")，安装包保留在缓存");
                return true;
            }
            // 安装失败则清掉缓存避免脏文件
            try { File.Delete(msi); } catch { }
            throw new Exception("Node.js 安装失败 (exit=" + p.ExitCode + ")");
        }

        // ================= npm / claude-code =================
        private static int RunCmd(string cmdExe, string args, out string stdout)
        {
            ProcessStartInfo psi = new ProcessStartInfo(cmdExe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.Default; // npm 在中文系统按代码页输出
            psi.StandardErrorEncoding = Encoding.Default;
            Process p = Process.Start(psi);
            stdout = p.StandardOutput.ReadToEndAsync().Result;
            string stderr = p.StandardError.ReadToEndAsync().Result;
            p.WaitForExit();
            if (!string.IsNullOrEmpty(stderr)) Console.WriteLine("    " + stderr.Trim());
            return p.ExitCode;
        }

        private static string RunNpm(string npmCmd, string args)
        {
            string stdout;
            // npm.cmd 是批处理，用 cmd /c 包装
            int code = RunCmd("cmd.exe", "/c \"\"" + npmCmd + "\" " + args + "\"\"", out stdout);
            foreach (string line in stdout.Trim().Split('\n'))
                if (!string.IsNullOrWhiteSpace(line)) Console.WriteLine("    " + line.Trim());
            if (code != 0) throw new Exception("npm " + args + " 失败 (exit=" + code + ")");
            return stdout;
        }

        private static string InstallClaudeCodeCli(string nodeExe)
        {
            Step("配置 npm 镜像并安装 @anthropic-ai/claude-code");
            // 确保 node 目录在 PATH（claude-code postinstall 会执行 `node install.cjs`，
            // 若 node 不在 PATH，npm 子进程 cmd 找不到 node 导致安装失败）
            EnsureNodeInPath(nodeExe);
            string nodeDir = Path.GetDirectoryName(nodeExe);
            string npmCmd = Path.Combine(nodeDir, "npm.cmd");
            if (!File.Exists(npmCmd))
                npmCmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "npm.cmd");
            if (!File.Exists(npmCmd)) throw new Exception("未找到 npm.cmd: " + npmCmd);

            try { RunNpm(npmCmd, "config set registry " + NpmRegistryMirror); }
            catch (Exception ex) { Warn("npm config 失败: " + ex.Message); }

            Console.WriteLine("  npm install -g @anthropic-ai/claude-code ...");
            // postinstall / 网络抖动可能失败，自动重试（实测第二次通常成功）
            bool installed = false;
            for (int attempt = 1; attempt <= 3 && !installed; attempt++)
            {
                try
                {
                    RunNpm(npmCmd, "install -g @anthropic-ai/claude-code");
                    installed = true;
                }
                catch (Exception ex)
                {
                    if (attempt < 3)
                    {
                        Warn(string.Format("npm install 第 {0} 次失败（{1}），自动重试...", attempt, ex.Message));
                        Thread.Sleep(2000);
                    }
                    else throw;
                }
            }
            Ok("claude-code 安装完成");

            string prefix = "";
            try
            {
                string so;
                RunCmd("cmd.exe", "/c \"" + npmCmd + "\" prefix -g", out so);
                string[] lines = so.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length > 0) prefix = lines[lines.Length - 1].Trim();
            }
            catch { }

            string[] cands = {
                Path.Combine(prefix, "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe"),
                Path.Combine(prefix, "@anthropic-ai", "claude-code", "bin", "claude.exe")
            };
            foreach (string c in cands) if (File.Exists(c)) { Ok("claude.exe: " + c); return c; }
            string baseDir = Path.Combine(prefix, "node_modules", "@anthropic-ai", "claude-code");
            if (Directory.Exists(baseDir))
            {
                string found = FindFileRecursive(baseDir, "claude.exe");
                if (found != null) { Ok("claude.exe: " + found); return found; }
            }
            throw new Exception("未找到 claude.exe（npm 安装目录: " + prefix + "）");
        }

        private static string FindFileRecursive(string dir, string fileName)
        {
            try
            {
                foreach (string f in Directory.GetFiles(dir, fileName))
                    if (Path.GetFileName(f).Equals(fileName, StringComparison.OrdinalIgnoreCase)) return f;
                foreach (string d in Directory.GetDirectories(dir))
                {
                    string r = FindFileRecursive(d, fileName);
                    if (r != null) return r;
                }
            }
            catch { }
            return null;
        }

        // ================= 版本确定（默认用 npm 最新版） =================
        private static string GetNpmPackageVersion(string cliExe)
        {
            try
            {
                string binDir = Path.GetDirectoryName(cliExe);        // .../claude-code/bin
                string pkgDir = Directory.GetParent(binDir).FullName; // .../claude-code
                string pj = Path.Combine(pkgDir, "package.json");
                if (File.Exists(pj))
                {
                    Match m = Regex.Match(File.ReadAllText(pj), "\"version\"\\s*:\\s*\"([^\"]+)\"");
                    if (m.Success) return m.Groups[1].Value;
                }
                // 回退：claude.exe --version
                ProcessStartInfo psi = new ProcessStartInfo(cliExe, "--version");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                Process p = Process.Start(psi);
                string v = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                foreach (string line in v.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    Match m2 = Regex.Match(line.Trim(), "(\\d+\\.\\d+\\.\\d+)");
                    if (m2.Success) return m2.Groups[1].Value;
                }
            }
            catch { }
            return "";
        }

        private static string FindRequiredVersion(string userData, string npmVersion)
        {
            // 1) Desktop 已初始化的版本目录（说明 Desktop 正在期待该版本，最可靠）
            string ccDir = Path.Combine(userData, "claude-code");
            if (Directory.Exists(ccDir))
            {
                string[] dirs = Directory.GetDirectories(ccDir);
                if (dirs.Length > 0) return Path.GetFileName(dirs[0]);
            }
            // 2) 否则直接用 npm 最新版
            return npmVersion;
        }

        // ================= 修复 + 重启 =================
        private static void InstallClaudeCodeBinary(string userData, string cliExe, string version)
        {
            Step("修复 Claude Code 二进制 (version=" + version + ")");
            string targetDir = Path.Combine(userData, "claude-code", version);
            Directory.CreateDirectory(targetDir);
            File.Copy(cliExe, Path.Combine(targetDir, "claude.exe"), true);
            File.WriteAllText(Path.Combine(targetDir, ".verified"), "");
            Ok("已复制 claude.exe 并创建 .verified");
            Ok("位置: " + targetDir);
        }

        // ================= API 中转配置（Tokeness.io 等 Anthropic 兼容端点） =================
        private static string SafeReadLine()
        {
            try { return Console.ReadLine() ?? ""; }
            catch { return ""; }
        }

        private static void ConfigureApi(string userData)
        {
            // 收尾空格统一清理（key/URL 粘贴时常带空格或换行）
            string baseUrl = ApiBaseUrl != null ? ApiBaseUrl.Trim() : "";
            if (string.IsNullOrEmpty(baseUrl)) baseUrl = DefaultApiBaseUrl;
            string apiKey = ApiKey != null ? ApiKey.Trim() : "";
            if (string.IsNullOrEmpty(apiKey) && !SkipApi)
            {
                Console.WriteLine();
                Console.Write("是否配置聚合 API？Key 请到 https://tokeness.io/keys 注册获取 [Y/n] ");
                string ans = SafeReadLine();
                // 默认 Y：直接回车即配置
                bool yes = string.IsNullOrWhiteSpace(ans) ||
                           ans.Trim().ToLower() == "y" || ans.Trim().ToLower() == "yes";
                if (yes)
                {
                    Console.Write("  中转 Base URL [默认 " + DefaultApiBaseUrl + "]: ");
                    string b = SafeReadLine();
                    if (!string.IsNullOrEmpty(b)) baseUrl = b.Trim();
                    Console.Write("  API Key（格式 sk-xxxxxx）: ");
                    apiKey = SafeReadLine().Trim();
                    if (string.IsNullOrEmpty(apiKey))
                    {
                        // 未输入 Key → 打开浏览器引导去获取页面注册
                        try { Process.Start("https://tokeness.io/keys"); } catch { }
                        Console.WriteLine("  已打开 https://tokeness.io/keys，请注册获取 Key 后粘贴：");
                        apiKey = SafeReadLine().Trim();
                    }
                    if (string.IsNullOrEmpty(ApiModels))
                    {
                        Console.Write("  模型 ID（回车使用官方最新 " + DefaultApiModels + "；或输入逗号分隔自定义）: ");
                        string m = SafeReadLine().Trim();
                        ApiModels = string.IsNullOrEmpty(m) ? DefaultApiModels : m;
                    }
                }
            }
            if (string.IsNullOrEmpty(apiKey))
            {
                if (!SkipApi) Warn("未提供 API Key，跳过聚合 API 配置（可加 -ApiKey <key> 或稍后重跑）");
                return;
            }
            if (!apiKey.StartsWith("sk-"))
                Warn("提示: 该平台 API Key 通常以 sk- 开头，请确认格式（示例 sk-xxxxxx）");
            if (string.IsNullOrEmpty(ApiModels)) ApiModels = DefaultApiModels;   // -ApiKey 参数直配时同样默认官方最新
            Step("配置聚合 API: " + baseUrl + "，模型: " + ApiModels);
            WriteRegistryConfig(baseUrl, apiKey, ApiModels);
            WriteClaudeCodeSettings(baseUrl, apiKey);
            Ok("聚合 API 配置完成。请完全退出并重启 Claude Desktop 生效（配置在启动时读取一次）。");
        }

        // Claude Desktop 3P 推理配置（官方 MDM 方式，聊天界面认这个）：
        //   HKCU\SOFTWARE\Policies\Claude（用户策略）
        //   HKLM\SOFTWARE\Policies\Claude（机器策略，存在时 HKCU 被忽略，需管理员）
        private static void WriteRegistryConfig(string baseUrl, string apiKey, string models)
        {
            bool hklmHasPolicy = false;
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Claude"))
                    hklmHasPolicy = k != null && k.GetValueNames().Length > 0;
            }
            catch { }
            try
            {
                Microsoft.Win32.RegistryKey root = hklmHasPolicy
                    ? Microsoft.Win32.Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Claude")
                    : Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Policies\Claude");
                using (root)
                {
                    root.SetValue("inferenceProvider", "gateway", Microsoft.Win32.RegistryValueKind.String);
                    root.SetValue("inferenceGatewayBaseUrl", baseUrl, Microsoft.Win32.RegistryValueKind.String);
                    root.SetValue("inferenceGatewayApiKey", apiKey, Microsoft.Win32.RegistryValueKind.String);
                    root.SetValue("inferenceGatewayAuthScheme", "bearer", Microsoft.Win32.RegistryValueKind.String);
                    // 可选：限定模型列表（官方要求数组/对象类型键以 JSON 字符串存 REG_SZ）
                    if (!string.IsNullOrEmpty(models))
                    {
                        string[] parts = models.Split(new char[] { ',', '，', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        StringBuilder sb = new StringBuilder("[");
                        for (int i = 0; i < parts.Length; i++)
                        {
                            if (i > 0) sb.Append(",");
                            sb.Append("\"").Append(parts[i].Trim().Replace("\"", "\\\"")).Append("\"");
                        }
                        sb.Append("]");
                        root.SetValue("inferenceModels", sb.ToString(), Microsoft.Win32.RegistryValueKind.String);
                    }
                }
                Ok((hklmHasPolicy ? "已写入机器策略 HKLM" : "已写入用户策略 HKCU") + @"\SOFTWARE\Policies\Claude（3P 推理 gateway）");
            }
            catch (Exception ex) { Warn("写入注册表策略失败: " + ex.Message); }
        }

        // 注意: 不再写 claude_desktop_config.json——该文件由 Claude Desktop 3P 部署模式自行管理，
        //       往其中注入非官方字段会导致 "Could not load app settings" 解析错误。
        //       3P 推理配置只通过注册表策略 (WriteRegistryConfig) 下发。

        private static void WriteClaudeCodeSettings(string baseUrl, string apiKey)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
                string p = Path.Combine(dir, "settings.json");
                JavaScriptSerializer ser = new JavaScriptSerializer();
                Dictionary<string, object> cfg = new Dictionary<string, object>();
                if (File.Exists(p))
                {
                    string text = File.ReadAllText(p);
                    try { cfg = ser.Deserialize<Dictionary<string, object>>(text) ?? new Dictionary<string, object>(); }
                    catch { Warn("~/.claude/settings.json 解析失败，将以新文件覆盖"); }
                }
                cfg["apiBaseUrl"] = baseUrl;
                cfg["apiKey"] = apiKey;
                Directory.CreateDirectory(dir);
                File.WriteAllText(p, ser.Serialize(cfg), Encoding.UTF8);
                Ok("已写入 Claude Code 配置: " + p);
            }
            catch (Exception ex) { Warn("写入 Claude Code 配置失败: " + ex.Message); }
        }

        private static string GetAumid(AppxInfo appx)
        {
            // AppId 不一定是 "App"（如 Claude 实际是 "Claude"），用 Get-StartApps 取真实 AUMID
            try
            {
                using (PowerShell ps = PowerShell.Create())
                {
                    ps.AddCommand("Get-StartApps");
                    foreach (PSObject r in ps.Invoke())
                    {
                        string appId = GetProp(r, "AppID");
                        if (!string.IsNullOrEmpty(appId) &&
                            appId.IndexOf(appx.PackageFamilyName, StringComparison.OrdinalIgnoreCase) >= 0)
                            return appId;
                    }
                }
            }
            catch { }
            return appx.PackageFamilyName + "!App"; // 兜底（部分应用 AppId 即 App）
        }

        private static void StartClaudeApp(AppxInfo appx)
        {
            if (appx == null) return;
            string aumid = GetAumid(appx);
            // 方式1: ShellExecute 直接解析 shell: 协议（标准做法）
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "shell:AppsFolder\\" + aumid;
                psi.UseShellExecute = true;
                Process.Start(psi);
                Ok("已启动 Claude Desktop");
                return;
            }
            catch (Exception ex)
            {
                Warn("shell:AppsFolder 启动失败: " + ex.Message + "，改用 explorer 启动");
            }
            // 方式2: explorer.exe 带引号执行 shell 命名空间（裸参数会被当普通路径打开文档目录）
            try
            {
                ProcessStartInfo psi2 = new ProcessStartInfo("explorer.exe", "\"shell:AppsFolder\\" + aumid + "\"");
                psi2.UseShellExecute = true;
                Process.Start(psi2);
                Ok("已启动 Claude Desktop (explorer)");
            }
            catch (Exception ex2) { Warn("explorer 启动也失败: " + ex2.Message); }
        }

        private static void CreateDesktopShortcut(AppxInfo appx)
        {
            try
            {
                string aumid = GetAumid(appx);
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string lnk = Path.Combine(desktop, "Claude Desktop.lnk");
                // WScript.Shell 创建 .lnk，目标为 shell:AppsFolder 命名空间
                string script =
                    "$ws = New-Object -ComObject WScript.Shell;" +
                    "$sc = $ws.CreateShortcut('" + lnk.Replace("'", "''") + "');" +
                    "$sc.TargetPath = 'shell:AppsFolder\\" + aumid + "';" +
                    "$sc.Description = 'Claude Desktop';" +
                    "$sc.Save()";
                using (PowerShell ps = PowerShell.Create())
                {
                    ps.AddScript(script);
                    ps.Invoke();
                    if (ps.HadErrors) throw new Exception(CollectErrors(ps));
                }
                Ok("已创建桌面快捷方式: " + lnk);
            }
            catch (Exception ex) { Warn("创建桌面快捷方式失败: " + ex.Message); }
        }

        private static void RestartClaude(AppxInfo appx)
        {
            Step("重启 Claude Desktop");
            foreach (Process pr in Process.GetProcessesByName("Claude"))
                try { pr.Kill(); } catch { }
            Thread.Sleep(2000);
            StartClaudeApp(appx);
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Claude Desktop 一键安装程序（国内网络优化版，纯 .NET 实现）");
            Console.WriteLine();
            Console.WriteLine("用法: ClaudeDesktop-Installer.exe [参数...]");
            Console.WriteLine();
            Console.WriteLine("参数:");
            Console.WriteLine("  -Force                     强制重装（先卸载旧版）");
            Console.WriteLine("  -SkipClaudeCode            只装桌面版，跳过 Claude Code 修复");
            Console.WriteLine("  -SkipNodeJs                使用系统已有 Node.js");
            Console.WriteLine("  -NodeVersion v24.19.0     指定 Node.js 版本（npmmirror）");
            Console.WriteLine("  -ClaudeCodeVersion 2.1.138 手动指定 claude-code 版本");
            Console.WriteLine("  -Mirror github|r2|official 下载源优先级（默认 github）");
            Console.WriteLine("  -SkipChecksum              跳过 SHA256 校验（不推荐）");
            Console.WriteLine("  -SkipApi                   跳过聚合 API 配置");
            Console.WriteLine("  -ApiBaseUrl <url>          聚合 API 地址（默认 https://n.tokeness.io）");
            Console.WriteLine("  -ApiKey <key>              API Key（https://tokeness.io/keys 注册获取）");
            Console.WriteLine("  -ApiModels <list>          模型 ID，逗号分隔（默认官方最新 claude-opus-5,claude-sonnet-5,claude-haiku-4-5）");
            Console.WriteLine();
            Console.WriteLine("无参数双击即可开始安装。");
        }
    }
}
