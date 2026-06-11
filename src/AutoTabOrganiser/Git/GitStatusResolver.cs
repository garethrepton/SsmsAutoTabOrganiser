using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace AutoTabOrganiser.Git
{
    internal enum GitFileStatus { Unknown, NotInRepo, Untracked, Modified, Staged, Clean }

    /// <summary>
    /// Resolves git status for a batch of file paths by grouping them by repo root and shelling
    /// out to `git status --porcelain` once per repo. Cached for ~30s to keep things cheap.
    /// </summary>
    internal sealed class GitStatusResolver
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, (DateTime when, Dictionary<string, GitFileStatus> map)> _byRepo
            = new Dictionary<string, (DateTime, Dictionary<string, GitFileStatus>)>(StringComparer.OrdinalIgnoreCase);
        // Very short TTL — we want the panel to feel live. Burst calls during a refresh
        // still hit cache (multiple paths grouped into one git invocation per repo), but
        // sequential refreshes after a real change always re-query.
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMilliseconds(200);

        public Dictionary<string, GitFileStatus> Resolve(IEnumerable<string> filePaths)
        {
            var result = new Dictionary<string, GitFileStatus>(StringComparer.OrdinalIgnoreCase);
            var byRepo = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in filePaths.Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(path)) { result[path] = GitFileStatus.NotInRepo; continue; }
                var repo = GitHelper.FindRepoRoot(Path.GetDirectoryName(path));
                if (repo == null) { result[path] = GitFileStatus.NotInRepo; continue; }
                if (!byRepo.TryGetValue(repo, out var list)) { list = new List<string>(); byRepo[repo] = list; }
                list.Add(path);
            }

            foreach (var pair in byRepo)
            {
                var statusMap = GetOrRefreshRepoStatus(pair.Key);
                foreach (var p in pair.Value)
                {
                    string rel;
                    try { rel = Path.GetFullPath(p).Substring(Path.GetFullPath(pair.Key).Length).TrimStart('\\', '/').Replace('\\', '/'); }
                    catch { rel = p; }
                    result[p] = statusMap != null && statusMap.TryGetValue(rel, out var st) ? st : GitFileStatus.Clean;
                }
            }
            return result;
        }

        private Dictionary<string, GitFileStatus> GetOrRefreshRepoStatus(string repoRoot)
        {
            lock (_gate)
            {
                if (_byRepo.TryGetValue(repoRoot, out var entry) && DateTime.UtcNow - entry.when < CacheTtl)
                    return entry.map;
            }
            var fresh = QueryRepoStatus(repoRoot);
            lock (_gate) _byRepo[repoRoot] = (DateTime.UtcNow, fresh);
            return fresh;
        }

        private static Dictionary<string, GitFileStatus> QueryRepoStatus(string repoRoot)
        {
            // Case-insensitive: git's output preserves on-disk casing but our locally-computed
            // relative paths (via Path.GetFullPath + Substring) sometimes pick up Windows's
            // canonicalised casing, which can differ from the actual on-disk casing in subtle
            // ways. Mismatched casing made Resolve() return Clean for genuinely-uncommitted
            // files, leaving the Stored Queries section empty.
            var map = new Dictionary<string, GitFileStatus>(StringComparer.OrdinalIgnoreCase);
            try
            {
                // --no-optional-locks: plain `git status` opportunistically refreshes the
                // index stat-cache, taking .git/index.lock each run. The tool window watches
                // the .git dir for changes, so that write made every status call schedule the
                // next refresh — a feedback loop pinning a refresh every ~150ms forever.
                var psi = new ProcessStartInfo("git", "--no-optional-locks status --porcelain -uall")
                {
                    WorkingDirectory = repoRoot,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return map;
                    // Drain stderr concurrently with stdout. Both pipes are redirected, and git
                    // can produce non-trivial stderr (warnings, "hint:" lines) on large repos.
                    // If we only read stdout, a full stderr pipe buffer blocks git, which then
                    // can't drain stdout, deadlocking both reads. ReadToEndAsync + Task.Run on
                    // stderr is cheap and prevents that.
                    var stderrTask = System.Threading.Tasks.Task.Run(() =>
                    {
                        try { return p.StandardError.ReadToEnd(); }
                        catch { return string.Empty; }
                    });
                    // stdout must be drained off-thread too: a synchronous ReadToEnd here
                    // blocks BEFORE WaitForExit, so the 2s kill never engaged while git was
                    // silently hung — the timeout only worked once output had already ended.
                    var stdoutTask = System.Threading.Tasks.Task.Run(() =>
                    {
                        try { return p.StandardOutput.ReadToEnd(); }
                        catch { return string.Empty; }
                    });
                    if (!p.WaitForExit(2000))
                    {
                        try { p.Kill(); } catch { }
                    }
                    var stdout = stdoutTask.Wait(500) ? stdoutTask.Result : string.Empty;
                    try { stderrTask.Wait(500); } catch { }
                    foreach (var raw in stdout.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var line = raw.TrimEnd('\r');
                        if (line.Length < 4) continue;
                        var staged   = line[0];
                        var unstaged = line[1];
                        var path     = line.Substring(3).Trim('"');
                        // For renames: "R  old -> new"
                        var arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
                        if (arrow > 0) path = path.Substring(arrow + 4);
                        var status = GitFileStatus.Clean;
                        if (staged == '?' && unstaged == '?') status = GitFileStatus.Untracked;
                        else if (unstaged != ' ' && unstaged != '\0') status = GitFileStatus.Modified;
                        else if (staged != ' ' && staged != '\0')    status = GitFileStatus.Staged;
                        map[path] = status;
                    }
                }
            }
            catch { }
            return map;
        }

        public void Invalidate()
        {
            lock (_gate) _byRepo.Clear();
        }
    }
}
