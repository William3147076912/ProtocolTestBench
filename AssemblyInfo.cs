using System.Windows;

// WPF 主题资源查找策略。
// 本项目没有按 Windows 主题拆分的 ResourceDictionary，所以 theme-specific 资源位置设为 None。
// 通用资源如果存在，会从当前程序集查找；这是 WPF SDK 模板的标准配置。
[assembly: ThemeInfo(
    ResourceDictionaryLocation.None,
    ResourceDictionaryLocation.SourceAssembly
)]
