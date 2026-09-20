using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UniSearch.Host.Settings;

namespace UniSearch.Host.Views;

/// <summary>
/// 设置窗口。编辑的是一份 <b>草稿副本</b>，点「保存」才交给宿主落盘并应用 —— 所以「取消」天然安全。
/// <para>
/// 为什么数值项不直接绑定：把 <c>int</c> 绑到 <c>TextBox.Text</c> 时，用户敲了非法内容，
/// WPF 的绑定引擎会<b>静默保留旧值</b>，用户以为改成功了。这里改成读字符串 + 显式校验报错。
/// </para>
/// </summary>
public partial class SettingsWindow : Window
{
    readonly UniSearchSettings _draft;
    readonly Func<UniSearchSettings, string?> _apply;
    readonly string _settingsDirectory;
    readonly string? _userFiltersPath;
    readonly string? _templateFiltersPath;

    /// <param name="current">当前生效的设置（会被克隆成草稿）。</param>
    /// <param name="apply">宿主提供的"落盘 + 即刻生效"回调，返回给用户看的结果说明。</param>
    /// <param name="settingsDirectory">设置文件所在目录（"打开位置"按钮用）。</param>
    /// <param name="filtersSummary">筛选器加载情况的一句话（几个、来自哪）。</param>
    /// <param name="userFiltersPath">用户自己的 filters.json（"编辑我的筛选器"用）。</param>
    /// <param name="templateFiltersPath">程序自带的 filters.json 模板（用户文件不存在时拿它初始化）。</param>
    public SettingsWindow(UniSearchSettings current, Func<UniSearchSettings, string?> apply, string settingsDirectory,
                          string? filtersSummary = null, string? userFiltersPath = null, string? templateFiltersPath = null)
    {
        InitializeComponent();
        _draft = current.Clone();
        _apply = apply;
        _settingsDirectory = settingsDirectory;
        _userFiltersPath = userFiltersPath;
        _templateFiltersPath = templateFiltersPath;

        DataContext = _draft;
        LoadDraftIntoForm();
        FiltersHint.Text = filtersSummary ?? "（没有读到筛选器定义）";
        Loaded += (_, _) => StatusLine.Text =
            $"设置保存在 {System.IO.Path.Combine(settingsDirectory, "settings.json")}";
    }

    /// <summary>
    /// 离屏截图的目标元素（--dump-settings 用）：直接渲染滚动区里的内容，
    /// 于是"比屏幕还高的设置页"能一张图拍全 —— 窗口本身最多只有屏幕那么高。
    /// </summary>
    internal FrameworkElement DumpContent => SettingsContent;

    void LoadDraftIntoForm()
    {
        MaxRowsBox.Text = _draft.Search.MaxRows.ToString();
        AutoProvidersBox.Text = string.Join(", ", _draft.Search.AutoSearchProviders);
        ExtraExcludeBox.Text = string.Join(Environment.NewLine, _draft.Search.ExtraExcludePaths);
        UpdateColumnsHint();

        UpdateHotkeyFieldsEnabled();
        UpdateCloseToTrayHint();
    }

    /// <summary>把"当前列布局"用列头名字说清楚 —— 配置里存的是 key，用户看的是中文列名。</summary>
    void UpdateColumnsHint()
    {
        var names = _draft.Columns.Visible
            .Select(k => ViewModels.ColumnCatalog.Find(k)?.Header)
            .Where(n => n is { Length: > 0 })
            .ToList();
        ColumnsHint.Text = names.Count == 0
            ? "当前列：默认（尚未自定义）。"
            : $"当前列：{string.Join(" · ", names)}（共 {names.Count} 列）";
    }

    // ── 热键 ────────────────────────────────────────────

    void OnHotkeysToggled(object sender, RoutedEventArgs e) => UpdateHotkeyFieldsEnabled();

    void UpdateHotkeyFieldsEnabled()
        => HotkeyFields.IsEnabled = HotkeysEnabledBox.IsChecked == true;

    void OnGestureFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box) box.SelectAll();
    }

    void OnGestureBlur(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box && string.IsNullOrWhiteSpace(box.Text))
            box.Text = "（未设置，不注册）";
    }

    void OnGestureKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;

        // Alt 组合键在 WPF 里 e.Key 报 Key.System，真键在 SystemKey（和主窗口同一个坑）
        var key = MainWindow.NormalizeKey(e.Key, e.SystemKey);
        e.Handled = true;

        // Delete / Backspace 清空 = 有意不注册这个热键
        if (key is Key.Delete or Key.Back)
        {
            box.Text = string.Empty;
            StatusLine.Text = "已清空该热键（保存后生效，留空 = 不注册）。";
            return;
        }

        // 按 Esc / Tab / Enter 时别把窗口操作也吃成热键
        if (key is Key.Escape or Key.Tab or Key.Enter) return;

        var gesture = Gestures.FromKeyboard(key, Keyboard.Modifiers);
        if (gesture is null)
        {
            StatusLine.Text = "这个组合不能用：至少需要一个修饰键（Ctrl / Alt / Shift / Win），功能键（F1–F24）除外。";
            return;
        }

        box.Text = gesture;
        StatusLine.Text = $"已记录组合键：{gesture}（保存后生效）";
    }

    // ── 托盘 ────────────────────────────────────────────

    void OnCloseToTrayToggled(object sender, RoutedEventArgs e) => UpdateCloseToTrayHint();

    void UpdateCloseToTrayHint()
    {
        CloseToTrayHint.Text = CloseToTrayBox.IsChecked == true
            ? "收进托盘后热键仍然可以唤回；退出程序请右键任务栏托盘图标 → 退出 UniSearch。"
            : "关闭按钮将直接退出程序（热键随之失效，需要重新启动）。托盘图标仍然可用。";
    }

    // ── 按钮 ────────────────────────────────────────────

    void OnSave(object sender, RoutedEventArgs e)
    {
        if (!TryReadNumbers(out var error))
        {
            StatusLine.Text = error;
            return;
        }

        // 把草稿里的热键串规范化（win+alt+space → Alt+Windows+Space）
        _draft.Hotkeys.Summon = Gestures.Normalize(_draft.Hotkeys.Summon) ?? string.Empty;
        _draft.Hotkeys.DirectoryScope = Gestures.Normalize(_draft.Hotkeys.DirectoryScope) ?? string.Empty;

        _draft.Search.ExtraExcludePaths = ExtraExcludeBox.Text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        _draft.Search.AutoSearchProviders = AutoProvidersBox.Text
            .Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant())
            .ToList();

        // 两个热键撞车要拦住：注册第二个必然失败，与其让用户看"被占用"，不如直接说清楚
        if (_draft.Hotkeys.Enabled
            && _draft.Hotkeys.Summon.Length > 0
            && string.Equals(_draft.Hotkeys.Summon, _draft.Hotkeys.DirectoryScope, StringComparison.OrdinalIgnoreCase))
        {
            StatusLine.Text = "两个热键不能是同一个组合，请改掉其中一个。";
            return;
        }

        var message = _apply(_draft);
        StatusLine.Text = message ?? "已保存。";
        DialogResult = true;
    }

    /// <summary>读数值框并校验。空字符串要单独认出来 —— <c>int.TryParse("")</c> 失败但报错信息不该是"格式不对"。</summary>
    bool TryReadNumbers(out string error)
    {
        if (!TryReadInt(MaxRowsBox.Text, "列表行数上限", 20, 20000, out var maxRows, out error)) return false;
        _draft.Search.MaxRows = maxRows;
        return true;
    }

    static bool TryReadInt(string? text, string label, int min, int max, out int value, out string error)
    {
        value = 0;
        var t = (text ?? string.Empty).Trim();
        if (t.Length == 0) { error = $"{label}不能为空。"; return false; }
        if (!int.TryParse(t, out value)) { error = $"{label}必须是整数，现在填的是「{t}」。"; return false; }
        if (value < min || value > max) { error = $"{label}要在 {min}–{max} 之间，现在填的是 {value}。"; return false; }
        error = string.Empty;
        return true;
    }

    void OnResetDefaults(object sender, RoutedEventArgs e)
    {
        var d = new UniSearchSettings();
        d.Normalize();

        // 只重置"设置页管得着"的部分，别把将来 Provider 私有的配置一起抹掉
        _draft.Hotkeys = d.Hotkeys;
        _draft.Search = d.Search;
        _draft.Preview = d.Preview;
        _draft.Window = d.Window;
        _draft.Columns = d.Columns;
        ColumnsEdited = true;

        DataContext = null;
        DataContext = _draft;
        LoadDraftIntoForm();
        StatusLine.Text = "已恢复默认值（还没保存）。点「保存」才会写入。";
    }

    /// <summary>
    /// 用户在<b>本窗口里</b>显式动过列布局（点了"恢复默认列布局"或整体恢复默认）。
    /// 宿主据此决定保存时要不要用草稿的列布局覆盖实时值 —— 没动过就用实时值，
    /// 否则会把用户在主窗口里刚拖出来的列宽回退掉。
    /// </summary>
    public bool ColumnsEdited { get; private set; }

    /// <summary>只把列布局恢复默认（列宽/顺序/可见列/排序）。和「恢复默认」一样，要保存才生效。</summary>
    void OnResetColumns(object sender, RoutedEventArgs e)
    {
        _draft.Columns = new ColumnsSettings();
        _draft.Columns.Normalize();
        ColumnsEdited = true;
        UpdateColumnsHint();
        StatusLine.Text = "列布局已恢复默认（还没保存）。点「保存」才会写入。";
    }

    void OnOpenSettingsFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _settingsDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StatusLine.Text = $"打不开目录：{ex.Message}";
        }
    }

    /// <summary>
    /// 打开"我的筛选器"。文件不存在时先用程序自带的模板初始化一份 ——
    /// 让用户面对一个空文件去想 JSON 怎么写，等于没提供这个功能。
    /// </summary>
    void OnEditMyFilters(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_userFiltersPath))
        {
            StatusLine.Text = "没有可编辑的筛选器文件路径。";
            return;
        }

        try
        {
            if (!File.Exists(_userFiltersPath))
            {
                if (!string.IsNullOrEmpty(_templateFiltersPath) && File.Exists(_templateFiltersPath))
                    File.Copy(_templateFiltersPath, _userFiltersPath);
                else
                    File.WriteAllText(_userFiltersPath, """{ "version": 1, "filters": [] }""");
            }

            Process.Start(new ProcessStartInfo { FileName = _userFiltersPath, UseShellExecute = true });
            StatusLine.Text = $"已打开 {_userFiltersPath} —— 改完保存，重启 UniSearch 生效。";
        }
        catch (Exception ex)
        {
            StatusLine.Text = $"打不开筛选器文件：{ex.Message}";
        }
    }

    void OnOpenFiltersFolder(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_userFiltersPath)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = System.IO.Path.GetDirectoryName(_userFiltersPath)!,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StatusLine.Text = $"打不开目录：{ex.Message}";
        }
    }

    void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>Esc 关窗；但焦点在热键输入框里时 Esc 只是"放弃这次录制"。</summary>
    void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (Keyboard.FocusedElement is TextBox { IsReadOnly: true })
        {
            Keyboard.ClearFocus();
            StatusLine.Text = "已放弃录制热键。";
            e.Handled = true;
            return;
        }
        DialogResult = false;
        e.Handled = true;
    }
}
