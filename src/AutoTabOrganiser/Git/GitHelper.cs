using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using AutoTabOrganiser.Util;

namespace AutoTabOrganiser.Git
{
    /// <summary>
    /// Thin wrapper over the `git` CLI. We never link a git library — VSIXes shouldn't carry
    /// libgit2; users already have git on PATH if they're using version control.
    /// All operations log stdout + stderr to the supplied logger and return a result struct.
    /// </summary>
    internal static class GitHelper
    {
        public static bool IsAvailable()
        {
            try
            {
                var psi = new ProcessStartInfo("git", "--version")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return false;
                    if (!p.WaitForExit(2000)) { try { p.Kill(); } catch { } return false; }
                    return p.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        public static string FindRepoRoot(string startDir)
        {
            if (string.IsNullOrEmpty(startDir)) return null;
            var dir = startDir;
            try
            {
                while (!string.IsNullOrEmpty(dir))
                {
                    if (Directory.Exists(Path.Combine(dir, ".git"))) return dir;
                    var parent = Directory.GetParent(dir)?.FullName;
                    if (parent == null || string.Equals(parent, dir, StringComparison.OrdinalIgnoreCase)) break;
                    dir = parent;
                }
            }
            catch { }
            return null;
        }

        public static bool IsTrackedFile(string filePath)
        {
            var repo = FindRepoRoot(Path.GetDirectoryName(filePath));
            return repo != null;
        }

        /// <summary>
        /// Escape a value for interpolation inside a double-quoted CLI argument. Windows
        /// paths can't legally contain '"', but DB-sourced or settings-sourced strings
        /// aren't filesystem-validated — defence in depth against argument injection.
        /// </summary>
        public static string EscapeArg(string value) => (value ?? "").Replace("\"", "\\\"");

        public static GitResult Add(string filePath, Logger log)
        {
            var dir = Path.GetDirectoryName(filePath);
            return Run(dir, $"add \"{EscapeArg(filePath)}\"", log);
        }

        public static GitResult Commit(string filePath, string message, Logger log)
        {
            var dir = Path.GetDirectoryName(filePath);
            var safeMsg = EscapeArg(message ?? "Update");
            return Run(dir, $"commit --only \"{EscapeArg(filePath)}\" -m \"{safeMsg}\"", log);
        }

        public static GitResult Status(string filePath, Logger log)
        {
            var dir = Path.GetDirectoryName(filePath);
            return Run(dir, $"status --porcelain \"{EscapeArg(filePath)}\"", log);
        }

        /// <summary>
        /// Diff a file against HEAD (covers both staged and unstaged changes). Untracked
        /// files return empty stdout — caller should fall back to a synthetic "all-added"
        /// diff for those, since git refuses to diff a path that isn't in HEAD or the index.
        /// </summary>
        public static GitResult Diff(string filePath, Logger log)
        {
            var dir = Path.GetDirectoryName(filePath);
            return Run(dir, $"diff HEAD -- \"{EscapeArg(filePath)}\"", log);
        }

        /// <summary>
        /// Current branch name plus commits ahead/behind the upstream. Read-only local
        /// plumbing (rev-parse / rev-list) — never touches the network. ahead/behind are -1
        /// when there is no upstream configured (the branch name alone is still returned).
        /// log is intentionally not taken: this runs on every panel refresh and would drown
        /// the daily log in identical entries.
        /// </summary>
        public static (string branch, int ahead, int behind) BranchStatus(string repoRoot)
        {
            var b = Run(repoRoot, "rev-parse --abbrev-ref HEAD", null);
            var branch = b.Ok ? (b.StdOut ?? "").Trim() : null;
            if (string.IsNullOrEmpty(branch) || branch == "HEAD") return (null, -1, -1); // detached or error

            var c = Run(repoRoot, "rev-list --left-right --count @{upstream}...HEAD", null);
            if (!c.Ok) return (branch, -1, -1); // typically: no upstream configured

            // Output: "<behind>\t<ahead>" (left = upstream-only commits, right = local-only).
            var parts = (c.StdOut ?? "").Trim().Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2
                && int.TryParse(parts[0], out var behind)
                && int.TryParse(parts[1], out var ahead))
                return (branch, ahead, behind);
            return (branch, -1, -1);
        }

        public static GitResult Run(string workingDir, string args, Logger log)
        {
            var result = new GitResult();
            try
            {
                var psi = new ProcessStartInfo("git", args)
                {
                    WorkingDirectory = workingDir ?? Environment.CurrentDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) { result.Error = "Failed to start git."; return result; }
                    var stdout = p.StandardOutput.ReadToEnd();
                    var stderr = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    result.ExitCode = p.ExitCode;
                    result.StdOut = stdout;
                    result.StdErr = stderr;
                }

                var summary = $"git {args} (in {workingDir})  -> exit {result.ExitCode}";
                log?.Info(summary);
                if (!string.IsNullOrWhiteSpace(result.StdOut)) log?.Info(result.StdOut.TrimEnd());
                if (!string.IsNullOrWhiteSpace(result.StdErr)) log?.Warn(result.StdErr.TrimEnd());
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                log?.Error("git invocation failed", ex);
            }
            return result;
        }
    }

    internal sealed class GitResult
    {
        public int ExitCode;
        public string StdOut;
        public string StdErr;
        public string Error;
        public bool Ok => string.IsNullOrEmpty(Error) && ExitCode == 0;
    }
}
