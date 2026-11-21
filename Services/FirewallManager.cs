using System;
using System.Diagnostics;
using System.Globalization;

namespace EftEndGuard.Services;

/// <summary>
/// 通过 netsh advfirewall 毫秒级添加/删除针对游戏主程序的临时出站阻断规则。
/// 用于“拒绝”时的断网双保险：在强杀前先阻断出站，确保卡住的结算包无法在我方销毁会话的瞬间“漏网”。
/// 规则带唯一名称前缀，可被 <see cref="RemoveAll"/> 在退出时兜底清理，绝不留残余。
/// </summary>
public sealed class FirewallManager
{
    private readonly string _rulePrefix;
    private readonly object _gate = new();
    private string? _activeRuleName;

    public FirewallManager(string ruleNamePrefix)
    {
        _rulePrefix = string.IsNullOrWhiteSpace(ruleNamePrefix) ? "EftEndGuard_TempBlock" : ruleNamePrefix.Trim();
    }

    /// <summary>
    /// 添加临时出站阻断规则（针对指定 exe；若 exe 路径未知则按程序名阻断所有匹配）。返回是否成功。
    /// 会先清理同名旧规则，保证幂等。
    /// </summary>
    public bool AddBlock(string executablePathOrName)
    {
        if (string.IsNullOrWhiteSpace(executablePathOrName))
            return false;

        lock (_gate)
        {
            try
            {
                var ruleName = $"{_rulePrefix}_{DateTime.Now:HHmmssfff}";
                // 先清理所有本工具残留规则，避免规则叠加
                RemoveAllInternal();

                // 依据是否为完整路径选择 program= 或 service 名称过滤
                var target = executablePathOrName.Trim();
                string programArg;
                if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    programArg = $"program=\"{target}\"";
                else
                    programArg = $"program=\"{target}.exe\"";

                // dir=out action=block：阻断出站
                var args =
                    $"advfirewall firewall add rule name=\"{ruleName}\" " +
                    $"dir=out action=block {programArg} enable=yes profile=any";

                var (ok, output) = RunNetsh(args);
                if (ok)
                {
                    _activeRuleName = ruleName;
                    return true;
                }

                Debug.WriteLine($"[FirewallManager] add rule failed: {output}");
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[FirewallManager] AddBlock error: " + ex);
                return false;
            }
        }
    }

    /// <summary>删除当前活动规则。</summary>
    public void RemoveActive()
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(_activeRuleName))
                return;

            try
            {
                RunNetsh($"advfirewall firewall delete rule name=\"{_activeRuleName}\"");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[FirewallManager] RemoveActive error: " + ex);
            }
            finally
            {
                _activeRuleName = null;
            }
        }
    }

    /// <summary>删除所有以本工具前缀命名的残留规则（退出兜底）。</summary>
    public void RemoveAll()
    {
        lock (_gate)
        {
            try { RemoveAllInternal(); }
            catch (Exception ex) { Debug.WriteLine("[FirewallManager] RemoveAll error: " + ex); }
            _activeRuleName = null;
        }
    }

    private void RemoveAllInternal()
    {
        // 枚举所有出站规则名，删除以本工具前缀命名的规则，实现真正的“万无一失”清理。
        // netsh show rule 会输出块格式，规则名在 "规则名称:" / "Rule Name:" 行。
        var (ok, output) = RunNetsh("advfirewall firewall show rule name=all dir=out");
        if (ok && !string.IsNullOrEmpty(output))
        {
            foreach (var rawLine in output.Split('\n'))
            {
                var line = rawLine.Trim();
                string? ruleName = null;

                // 中文 / 英文 netsh 输出的规则名标签
                int idx = line.IndexOf("规则名称:", StringComparison.Ordinal);
                if (idx >= 0)
                    ruleName = line[(idx + "规则名称:".Length)..].Trim();
                else
                {
                    idx = line.IndexOf("Rule Name:", StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                        ruleName = line[(idx + "Rule Name:".Length)..].Trim();
                }

                if (!string.IsNullOrEmpty(ruleName)
                    && ruleName.StartsWith(_rulePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    RunNetsh($"advfirewall firewall delete rule name=\"{ruleName}\"");
                }
            }
        }

        // 兜底：精确删除当前已知活动规则
        if (!string.IsNullOrEmpty(_activeRuleName))
        {
            RunNetsh($"advfirewall firewall delete rule name=\"{_activeRuleName}\"");
        }
        _activeRuleName = null;
    }

    private static (bool ok, string output) RunNetsh(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "netsh.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        using var p = Process.Start(psi);
        if (p is null)
            return (false, "netsh 启动失败");

        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        var exited = p.WaitForExit(3000);
        if (!exited)
        {
            try { p.Kill(); } catch { }
            return (false, "netsh 超时");
        }

        // netsh 成功时 ExitCode == 0；中文系统输出 "确定"/"Ok"
        bool ok = p.ExitCode == 0;
        return (ok, stdout + stderr);
    }
}

