using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AFKLocker.Tests
{
    internal static class CiWorkflowTests
    {
        private static string Root
        {
            get
            {
                var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
                while (directory != null && !File.Exists(Path.Combine(directory.FullName, ".github/workflows/ci.yml")))
                    directory = directory.Parent;
                Assert.NotNull(directory, "run the tests inside the source checkout");
                return directory.FullName;
            }
        }

        private static string Read(string path)
        {
            return File.ReadAllText(Path.Combine(Root, path)).Replace("\r\n", "\n");
        }

        private static string Job(string workflow, string id)
        {
            var match = Regex.Match(workflow, "^  " + id + @":\n(.*?)(?=^  [\w-]+:|\z)",
                RegexOptions.Multiline | RegexOptions.Singleline);
            Assert.True(match.Success, "job exists: " + id);
            return match.Groups[1].Value;
        }

        [Test("CI keeps the Windows build and exposes the standard required checks")]
        private static void WorkflowContract()
        {
            string workflow = Read(".github/workflows/ci.yml");
            Assert.True(workflow.StartsWith("# CI profile: public\nname: CI\n"), "public CI profile");
            Assert.Equal("ci.yml,dependabot-automerge.yml,release.yml",
                string.Join(",", Directory.GetFiles(Path.Combine(Root, ".github/workflows"), "*.yml")
                    .Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal)), "only the old safety workflow moves");
            Assert.Equal("Safety,Build,CI passed", string.Join(",", Regex.Matches(workflow, @"^    name: (.+)$",
                RegexOptions.Multiline).Cast<Match>().Select(match => match.Groups[1].Value)), "stable check names");
            Assert.True(workflow.Contains("  push:\n    branches: [main]"), "main pushes");
            Assert.True(workflow.Contains("  pull_request:\n    branches: [main]\n    types: [opened, synchronize, reopened, ready_for_review]"), "PR triggers retained");
            Assert.True(workflow.Contains("  workflow_dispatch:"), "manual runs");
            Assert.True(workflow.Contains("\npermissions:\n  contents: read\n"), "read-only default");
            Assert.True(workflow.Contains("group: ${{ github.workflow }}-${{ github.event.pull_request.number || github.ref }}"), "concurrency group");
            Assert.True(workflow.Contains("cancel-in-progress: ${{ github.event_name == 'pull_request' }}"), "only PRs cancelled");
            Assert.False(workflow.Contains("pull_request_target"), "no privileged PR execution");
            Assert.False(workflow.Contains("secrets."), "no PR secrets");
            foreach (Match action in Regex.Matches(workflow, @"uses: (.+)", RegexOptions.Multiline))
                Assert.True(Regex.IsMatch(action.Value, @"uses: actions/[\w-]+@[0-9a-f]{40} +# v[\d.]+$"), "immutable action: " + action.Value);

            string build = Job(workflow, "build");
            Assert.True(build.Contains("runs-on: windows-latest\n    timeout-minutes: 2"), "Windows and original timeout");
            Assert.False(build.Contains("    if:"), "build is unconditional");
            Assert.True(build.Contains("run: ./tools/Build.ps1 -Test -Installer -Iscc \"C:\\Program Files (x86)\\Inno Setup 6\\ISCC.exe\""), "installer build preserved");
            foreach (string step in new[] { "Check versions agree", "Verify icon assets are reproducible", "Upload build output" })
                Assert.True(build.Contains("name: " + step), "build step preserved: " + step);
            string gate = Job(workflow, "ci-passed");
            Assert.True(gate.Contains("needs: [safety, build]"), "gate waits for every job");
            Assert.True(gate.Contains("if: always()"), "gate reports failures and cancellations");
            Assert.True(gate.Contains("timeout-minutes: 1"), "bounded gate");
        }

        [Test("Safety executes the base policy with a one-time bootstrap and pinned secret scanner")]
        private static void SafetyContract()
        {
            string safety = Job(Read(".github/workflows/ci.yml"), "safety");
            foreach (string required in new[] {
                "if: github.event_name == 'pull_request'", "timeout-minutes: 3", "pull-requests: read",
                "ref: ${{ github.event.pull_request.base.sha }}", "sparse-checkout: .github/scripts",
                "if: hashFiles('base-policy/.github/scripts/safety.sh') == ''", "fetch-depth: 0",
                "bash base-policy/.github/scripts/safety.sh", "bash bootstrap-policy/.github/scripts/safety.sh" })
                Assert.True(safety.Contains(required), "safety contract: " + required);
            Assert.Equal(3, Regex.Matches(safety, "persist-credentials: false").Count, "all checkouts avoid credentials");
            string script = Read(".github/scripts/safety.sh");
            foreach (string required in new[] {
                "set -euo pipefail", "/v8.21.2/gitleaks_8.21.2_linux_x64.tar.gz",
                "5bc41815076e6ed6ef8fbecc9d9b75bcae31f39029ceb55da08086315316e3ba",
                "sha256sum -c -", "--ignore-gitleaks-allow",
                "--log-opts=\"--text --no-textconv --no-ext-diff --diff-merges=first-parent $BASE..$HEAD_SHA\"",
                "gh api --paginate",
                ".previous_filename", "safety-critical.txt?ref=$BASE",
                @"^\.github/(scripts/safety\.sh|safety-critical\.txt|workflows/ci\.yml)$",
                "invalid critical-path rule on the base revision" })
                Assert.True(script.Contains(required), "safety script: " + required);
        }

        private const string FakeHead = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        private static string GitBash()
        {
            foreach (string variable in new[] { "ProgramFiles", "ProgramFiles(x86)" })
            {
                string programs = Environment.GetEnvironmentVariable(variable);
                if (string.IsNullOrEmpty(programs)) continue;
                string candidate = Path.Combine(programs, "Git", "bin", "bash.exe");
                if (File.Exists(candidate)) return candidate;
            }
            throw new AssertionException("Git for Windows bash is required to run safety.sh");
        }

        private static void WriteScript(string path, string body)
        {
            File.WriteAllText(path, "#!/usr/bin/env bash\nset -eu\n" + body.Replace("\r\n", "\n"), new UTF8Encoding(false));
        }

        // Runs the real safety.sh against fake curl/tar/gitleaks/git/gh; returns exit code, output and the scan arguments.
        private static string[] RunPolicy(string plantGitleaks, string baseSha, string headSha, string changedFiles)
        {
            string work = Path.Combine(Path.GetTempPath(), "afklocker-safety-" + Guid.NewGuid().ToString("N"));
            string fake = Path.Combine(work, "bin");
            Directory.CreateDirectory(fake);
            try
            {
                if (plantGitleaks != null)
                    foreach (string name in new[] { ".gitleaksignore", ".gitleaks.toml" })
                        File.WriteAllText(Path.Combine(work, name), "allow everything\n");
                WriteScript(Path.Combine(fake, "curl"), "exit 0\n");
                WriteScript(Path.Combine(fake, "sha256sum"), "cat >/dev/null\nexit 0\n");
                WriteScript(Path.Combine(fake, "tar"), "cp \"$FAKE_GITLEAKS\" \"$4/gitleaks\"\nchmod +x \"$4/gitleaks\"\n");
                WriteScript(Path.Combine(fake, "gitleaks"),
                    "printf \"%s\\n\" \"$*\" > \"$SCAN_ARGS\"\n" +
                    "[ -e .gitleaksignore ] || [ -e .gitleaks.toml ] && echo present >> \"$SCAN_ARGS\"\n" +
                    "exit 0\n");
                WriteScript(Path.Combine(fake, "git"), "printf '%s\\n' \"$HEAD_SHA_FAKE\"\n");
                WriteScript(Path.Combine(fake, "gh"),
                    "case \"$*\" in\n" +
                    "  *'/pulls/'*) printf '%s\\n' \"$CHANGED_FILES\" ;;\n" +
                    "  *'/contents/.github?ref='*) printf '%s\\n' \"$BASE_ENTRIES\" ;;\n" +
                    "  *) exit 99 ;;\n" +
                    "esac\n");
                string runner = Path.Combine(work, "run.sh");
                File.WriteAllText(runner, "export PATH=\"$(cd \"$FAKE_BIN\" && pwd):$PATH\"\nexec bash .github/scripts/safety.sh\n", new UTF8Encoding(false));

                var start = new ProcessStartInfo(GitBash(), "\"" + runner.Replace('\\', '/') + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Root
                };
                var env = start.EnvironmentVariables;
                env["FAKE_BIN"] = fake.Replace('\\', '/');
                env["PR_DIR"] = work.Replace('\\', '/');
                env["TMPDIR"] = work.Replace('\\', '/');
                env["REPO"] = "test/repo";
                env["PR"] = "123";
                env["BASE"] = baseSha;
                env["HEAD_SHA_FAKE"] = headSha;
                env["FAKE_GITLEAKS"] = Path.Combine(fake, "gitleaks").Replace('\\', '/');
                env["SCAN_ARGS"] = Path.Combine(work, "scan-args").Replace('\\', '/');
                env["CHANGED_FILES"] = changedFiles;
                env["BASE_ENTRIES"] = "scripts\nworkflows";
                using (var process = Process.Start(start))
                {
                    if (!process.WaitForExit(30000))
                    {
                        process.Kill();
                        throw new AssertionException("safety.sh timed out.");
                    }
                    string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                    string argsFile = Path.Combine(work, "scan-args");
                    return new[] { process.ExitCode.ToString(), output, File.Exists(argsFile) ? File.ReadAllText(argsFile) : "" };
                }
            }
            finally
            {
                Directory.Delete(work, true);
            }
        }

        [Test("Safety scan uses the built-in rules, ignores gitleaks:allow and scans BASE..HEAD")]
        private static void SafetyScanArguments()
        {
            string[] run = RunPolicy(null, new string('a', 40), FakeHead, "docs/test.md");
            Assert.Equal("0", run[0], run[1]);
            foreach (string required in new[] { "--ignore-gitleaks-allow", "--config ", "--gitleaks-ignore-path ",
                "--log-opts=--text --no-textconv --no-ext-diff --diff-merges=first-parent " + new string('a', 40) + ".." + FakeHead })
                Assert.True(run[2].Contains(required), "scan argument: " + required);
            Assert.True(run[1].Contains("no critical effect"), "ordinary change is not critical");
        }

        [Test("A PR cannot configure its own scanner")]
        private static void PrCannotConfigureItsOwnScanner()
        {
            // The PR's .gitleaksignore/.gitleaks.toml are gone before gitleaks runs,
            // and adding or changing them (or .gitattributes) needs the owner.
            string[] run = RunPolicy("plant", new string('a', 40), FakeHead, "docs/test.md");
            Assert.Equal("0", run[0], run[1]);
            Assert.False(run[2].Contains("present"), "planted scanner files are removed before the scan");
            foreach (string path in new[] { ".gitleaksignore", "sub/.gitleaks.toml", ".gitattributes" })
            {
                run = RunPolicy(null, new string('a', 40), FakeHead, path);
                Assert.Equal("1", run[0], run[1]);
                Assert.True(run[1].Contains("changes what the secret scan sees"), "needs owner: " + path);
            }
        }

        [Test("Invalid base or head commit ids fail closed")]
        private static void InvalidCommitIdsFailClosed()
        {
            foreach (string[] ids in new[] { new[] { "main", FakeHead }, new[] { new string('a', 40), "HEAD" } })
            {
                string[] run = RunPolicy(null, ids[0], ids[1], "docs/test.md");
                Assert.Equal("1", run[0], run[1]);
                Assert.True(run[1].Contains("invalid base or head commit"), "rejects " + ids[0] + " / " + ids[1]);
            }
        }

        [Test("The actual aggregate command rejects failed, skipped and cancelled jobs except non-PR Safety")]
        private static void AggregateResults()
        {
            string gate = Job(Read(".github/workflows/ci.yml"), "ci-passed");
            string command = gate.Substring(gate.IndexOf("        run: |\n", StringComparison.Ordinal) + "        run: |\n".Length);
            var test = new StringBuilder("$gate = {\n" + command + "\n}\n");
            // Execute the workflow's command, rather than a second implementation of its rules.
            test.AppendLine(@"
$ErrorActionPreference = 'Stop'
foreach ($eventName in 'pull_request', 'push', 'workflow_dispatch') {
  foreach ($safety in 'success', 'failure', 'skipped', 'cancelled') {
    foreach ($build in 'success', 'failure', 'skipped', 'cancelled') {
      $expected = $build -eq 'success' -and ($safety -eq 'success' -or ($safety -eq 'skipped' -and $eventName -eq 'push'))
      $env:EVENT_NAME = $eventName
      $env:NEEDS_JSON = @{ safety = @{ result = $safety }; build = @{ result = $build } } | ConvertTo-Json -Compress
      $passed = $true
      try { & $gate } catch { $passed = $false }
      if ($passed -ne $expected) { throw ('Unexpected gate result: ' + $eventName + '/' + $safety + '/' + $build) }
    }
  }
}");
            test.AppendLine("exit 0");
            var start = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -EncodedCommand " +
                Convert.ToBase64String(Encoding.Unicode.GetBytes(test.ToString())))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var process = Process.Start(start))
            {
                if (!process.WaitForExit(30000))
                {
                    process.Kill();
                    throw new AssertionException("Aggregate test timed out.");
                }
                Assert.Equal(0, process.ExitCode, process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd());
            }
        }
    }
}
