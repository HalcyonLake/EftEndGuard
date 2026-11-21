# EFT EndGuard · 战局结算守卫

轻量级本地 HTTPS 流量观测与战局结算确认工具（C# / .NET 8 / WPF）。

## 原理（纯外部 L7，零进程侵入）

作为本地显式代理运行，仅对 `gw-pve.escapefromtarkov.com` 开启 SSL 解密，其余系统流量全部透传。
当捕获 `POST /client/match/local/end`（含 `?retry=1` 等变体，使用 `Contains` 匹配）时：

- **卡包**：请求在 `BeforeRequest` 中挂起（不返回、不失败），弹出置顶确认框（倒计时 + 提示音 + 临时抢焦点）。
  全程**只读 request-line 与少量请求头，绝不触碰 Body**；放行时原始字节流原样转发。
- **允许上传**：释放卡包，请求正常发往服务器完成结算。
- **拒绝（回滚）**：置位熔断 `_isAbandoning` →（可选）防火墙临时阻断出站 → `Process.Kill` 强杀游戏进程 →
  `TerminateSession()` 销毁已卡住的请求 → 冷却 2 秒后复位熔断。
  熔断期间任何重试/并发结算包被直接掐断，不弹窗、不放行，防止客户端补发。

> 命中判定仅限 `POST/GET/PUT/PATCH/DELETE` 等业务方法，排除 `CONNECT/HEAD/OPTIONS/TRACE`，
> 防止把 HTTPS 隧道握手或预检请求误判为结算。

## 功能概览

### 基础功能
- **根证书信任状态**：主界面实时显示本地根证书是否已安装并受系统信任；提供「一键修复 / 重装证书」按钮，
  以管理员身份重新调用 `EnsureRootCertificate(true,true,true)` 并回读机器级/用户级信任状态。
- **日志落盘**：所有拦截 / 放行 / 强杀 / 防火墙 / 异常信息按日期写入
  `程序目录\logs\yyyy-MM-dd.log`（线程安全、跨天自动切换、写入失败静默降级）。
  主界面「打开日志目录」按钮一键打开该目录。
- **配置持久化**：配置以 **XML（`app.config`）** 形式保存到**程序所在目录**（便携式，无需 %AppData%）。
  界面每次编辑经 600ms 防抖后自动落盘；采用「临时文件 + `File.Replace`」原子写，防止写坏。
  文件缺失 / 损坏 / 校验失败时回退默认配置。
- **单实例**：`Global\` 命名互斥体 + 本用户命名互斥体双重探测；重复启动时通过自定义窗口消息
  让已运行实例从托盘恢复并前置，然后本进程退出，避免代理 / 证书 / 系统代理状态冲突。
- **托盘常驻**：关闭主窗口后驻留托盘，右键菜单可显示窗口 / 启停代理 / 退出（退出时还原系统代理）。

### 确认弹窗：临时抢焦点 + 鼠标穿透 + 窗口级按键 + 全局热键兜底

> 结算接口 `/client/match/local/end` 触发时对局已结束（死亡或撤离），游戏无需键盘输入，
> 因此弹窗采用**临时抢焦点**策略，确保能覆盖显示在独占全屏 / 全屏窗口化画面之上。

- `ShowActivated="True"`、`Focusable="True"`、`Topmost="True"`，并在 `SourceInitialized` 时通过
  `SetWindowLong(GWL_EXSTYLE, WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW)` 注入扩展样式：
  **鼠标点击完全穿透**（避免误点游戏），且不计入 Alt+Tab。
- **抢焦点**：`StealFocus()` 通过 `Activate + Topmost 刷新 + SetForegroundWindow` 多次重试以对抗全屏切换；
  `_refocusTimer` 每 500ms 在弹窗失焦且未决策时自动把焦点拉回（对抗玩家点击游戏抢焦点）。
- **输入双路径**：
  - 主路径：窗口级 `PreviewKeyDown / PreviewKeyUp`（抢到焦点后由 WPF 直接派发）；
  - 兜底路径：仅在弹窗挂起期间安装 `WH_KEYBOARD_LL` 低级键盘钩子（窗口未拿到键时），
    弹窗关闭立即 `UnhookWindowsHookEx` 卸载。
  - 两条路径共用同一处理逻辑，用「按住中」边沿标志去重；热键按下会被吞掉（不会传给游戏）。
- **防抖静默期**：弹窗出现后 `InputArmDelayMs`（默认 500ms）内忽略 F9/F10，防止黑屏瞬间激动误触。
- 可配置：`AllowHotkey`（默认 F9）、`RejectHotkey`（默认 F10）、`HotkeyTriggerMode`
  （`SinglePress` 单击即触发 / `Hold` 长按触发）、`HoldDurationMs`（默认 800ms）。
- 长按模式下弹窗 UI 实时显示允许/拒绝两侧进度条（16ms 刷新），松开立即重置。
  （低级钩子的按键自动重复已用边沿标志规避，进度不会中途归零抖动。）
- 倒计时结束按配置的 `TimeoutAction` 执行；用户点 X 关闭窗口一律按「拒绝」处理，防止误上传。

### 拒绝时的断网双保险
- 核心顺序不变：**卡住包 → 置熔断 `_isAbandoning` → (可选)防火墙阻断 → 强杀进程 → 销毁代理会话 → 冷却 2s 复位熔断**。
- 可选 `EnableFirewallBlockOnReject`：开启后，在**强杀之前**先用 `netsh advfirewall` 毫秒级添加一条
  `dir=out action=block` 的临时规则，阻断 `EscapeFromTarkov.exe` 出站；冷却结束自动删除该规则。
- **不触碰系统网络适配器**（避免影响语音与慢恢复）。
- `SystemProxyGuard.Restore()` / `EmergencyCleanup` 退出兜底时会调用 `FirewallManager.RemoveAll()`
  枚举并删除所有以本工具前缀命名的残留规则，确保万无一失。
- 强杀带三段观测报告（`ProcessKillReport`）：分别记录「请求(枚举) / 进程退出 / 窗口消失」，便于排障。
- 首轮直接 `Process.Kill(entireProcessTree: true)` 快杀，仍有存活则 `CloseMainWindow + Kill` 二次兜底。

## 技术栈与包

- 目标框架：`net8.0-windows`（WPF + WinForms 托盘）
- `Titanium.Web.Proxy` **3.2.3**（NuGet 上唯一带 `net8.0` 目标、且可在 net8 下正常使用的版本；5.x 需要 .NET 10）
- `app.manifest`：`asInvoker`（在非管理员环境也能启动并弹出友好提示，见下方「安全红线」）

## 架构分层

| 层 | 职责 | 关键文件 |
|---|---|---|
| App | 组装（手工 DI）、单实例、托盘、全局退出兜底、异常处理 | `App.xaml.cs` |
| Interfaces | 服务抽象（`IProxyEngineService` / `IProcessManagerService` / `IUserConfirmService`） | `Interfaces/*` |
| Services | 代理引擎、进程管理、系统代理守卫、防火墙、热键、日志、配置 | `Services/*` |
| ViewModels | 主界面状态、确认弹窗逻辑、`RelayCommand` | `ViewModels/*` |
| Models | `AppConfig` + 决策 / 报告 / 日志 DTO | `Models/*` |
| Views | 主窗口、确认弹窗、转换器 | `Views/*` |

## 安全红线

1. **零进程侵入**：仅标准网络代理 + Win32 进程管理，无 DLL 注入 / API Hook / 内存读写。
2. **系统代理防残留（最高优先级）**：`SystemProxyGuard` 启动时快照 WinINet 配置（WinINet + 注册表双写并读回校验），
   在正常退出、`DispatcherUnhandledException`、`AppDomain.ProcessExit`、`SessionEnding`、ConsoleCtrl 等多条路径幂等还原；
   失败时再走注册表兜底，绝不留下 `127.0.0.1` 导致断网。还会识别并清除 `ProxyServer="1"` 这类历史坏值。
3. **仅解密目标域名**：`BeforeTunnelConnectRequest` 中仅对目标 Host 置 `DecryptSsl = true`，其余 `false` 透传，
   绝不 MITM 任何第三方 HTTPS（否则 Steam / 浏览器等强校验证书的服务会失败）。
4. **不触达请求体**：命中判定仅依赖 Host + Path + Method，弹窗展示仅用 request-line 与少量非敏感头，
   `Authorization / Cookie / X-*-Token` 等敏感头一律脱敏，绝不落日志或展示。
5. **管理员权限闸门**：`app.manifest` 采用 `asInvoker`；`App.OnStartup` 启动即检测是否管理员，
   非管理员弹出「需要权限 / Need permission」双语提示后退出（避免与提权实例并存导致状态冲突）。
   强杀受保护进程 / 写机器级证书 / 系统代理兜底等行为均需以管理员运行。

## ⚠️ 合规与风险评估（务必阅读）

本工具“拒绝回档”的语义是在结算上传瞬间从外部强行掐断已认证会话并强杀进程。从 ToS / 反作弊角度看：

- 外部程序强制中止游戏网络会话并击杀进程，在网络层行为上可能被 BattlEye 判定为 **网络篡改 / 会话劫持** 类风险行为；
- 通过 suppressing end-match 数据包实现“回滚”，本质是对 **在线 PVE 结算结果的数据完整性干预**，PVE 模式下仍可能被账户处置。

请在**仅测试环境、自有账号、明确知晓风险**的前提下使用。切到更安全的“仅观测 / 允许确认”模式只需在
`ProxyEngineService.HandleInterceptAsync` 中将拒绝分支改为直接放行即可。

## 运行

```powershell
dotnet build -c Release
# 以管理员身份运行 bin\Release\net8.0-windows\EftEndGuard.exe
```

首次启动会安装并信任根证书（用于解密目标域名 HTTPS），并在**程序目录**下生成配置文件 `app.config`（XML）。
日志默认写入**程序目录**下的 `logs\yyyy-MM-dd.log`。
## 运行截图
<img width="1268" height="906" alt="QQ_2026-11-21_21-40-41" src="https://github.com/user-attachments/assets/e0f23f60-68d3-4ff2-af19-ce852b690ed5" />
<img width="1046" height="902" alt="A2BB4420941C9F2384BF3CDAE76A3089" src="https://github.com/user-attachments/assets/41698a43-bd81-46b4-a17f-c9e5941b5b8b" />
<img width="440" height="217" alt="QQ_2026-11-21_21-43-18" src="https://github.com/user-attachments/assets/17931a3b-46db-4cc7-9412-e7226d347b8e" />


