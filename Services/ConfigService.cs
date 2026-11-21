using System;
using System.IO;
using System.Xml.Serialization;
using EftEndGuard.Models;

namespace EftEndGuard.Services;

/// <summary>
/// 配置读写服务：以 XML 形式保存到程序目录下的 <c>app.config</c>（无论是否已存在都会按需创建）。
///   - 读取：反序列化 app.config；文件缺失/损坏时回退默认配置并立即写出。
///   - 保存：接住界面的每一次编辑（配合 AppConfig 的 INotifyPropertyChanged 由 ViewModel 防抖触发）。
/// 选择程序目录而非 %AppData%，便于“绿色便携”，也符合用户“在当前文件夹读写 app.config”的要求。
/// </summary>
public sealed class ConfigService
{
    private readonly string _configDir;
    private readonly string _configPath;
    private readonly object _gate = new();

    private static readonly XmlSerializer Serializer = new(typeof(AppConfig));

    public ConfigService(string? configDirectory = null)
    {
        // 默认写入“程序当前所在目录”，而不是受保护的系统目录
        _configDir = configDirectory ?? AppContext.BaseDirectory;
        _configPath = Path.Combine(_configDir, "app.config");
    }

    public string ConfigFilePath => _configPath;

    /// <summary>配置在磁盘上变化（被外部编辑）时触发，便于热更新。</summary>
    public event EventHandler<AppConfig>? ConfigChanged;

    /// <summary>加载配置；文件不存在或损坏时返回默认配置并落盘。</summary>
    public AppConfig Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    using var stream = File.OpenRead(_configPath);
                    if (Serializer.Deserialize(stream) is AppConfig cfg)
                    {
                        var err = cfg.Validate();
                        return err is null ? cfg : FallbackToDefaults($"配置校验失败（{err}），已回退默认值。");
                    }
                }
            }
            catch (Exception)
            {
                // 损坏的配置不应阻止程序启动
            }

            var defaults = new AppConfig();
            TrySave(defaults);
            return defaults;
        }
    }

    public bool TrySave(AppConfig config)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_configDir);

                // 原子写：先写临时文件再替换，防止写一半崩溃导致配置损坏
                var tmp = _configPath + ".tmp";
                using (var stream = File.Create(tmp))
                {
                    Serializer.Serialize(stream, config);
                }

                if (File.Exists(_configPath))
                    File.Replace(tmp, _configPath, null);
                else
                    File.Move(tmp, _configPath);

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ConfigService] save failed: " + ex);
                return false;
            }
        }
    }

    /// <summary>请求主流程重新加载配置，并通知订阅者（热更新）。</summary>
    public AppConfig Reload()
    {
        var cfg = Load();
        ConfigChanged?.Invoke(this, cfg);
        return cfg;
    }

    private AppConfig FallbackToDefaults(string reason)
    {
        var defaults = new AppConfig();
        TrySave(defaults);
        System.Diagnostics.Debug.WriteLine("[ConfigService] " + reason);
        return defaults;
    }
}
