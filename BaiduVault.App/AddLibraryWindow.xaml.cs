using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace BaiduVault.App;

public partial class AddLibraryWindow : Window
{
    private readonly OpenListClient _ol;
    private readonly ServiceManager _svc;
    private readonly LibraryManager _mgr;
    private readonly bool _editMode;
    private readonly string? _origName;

    private CloudPreset? _preset;
    private string? _manualDriver;
    private bool _suppressPreset;
    private bool _initialized;
    private OlDriverInfo? _info;
    private readonly Dictionary<string, Control> _controls = new();
    private readonly Dictionary<string, string> _existingAddition = new(StringComparer.OrdinalIgnoreCase);
    private string? _existingDriver;
    private string? _existingRemark;

    public string? LibraryName { get; private set; }

    public AddLibraryWindow(OpenListClient ol, ServiceManager svc, LibraryManager mgr, string? editName = null)
    {
        InitializeComponent();
        _ol = ol;
        _svc = svc;
        _mgr = mgr;
        _editMode = editName != null;
        _origName = editName;

        foreach (var p in CloudPresets.All) PresetCombo.Items.Add(p.ComboText);
        PresetCombo.Items.Add("（其它驱动：选此项后点「高级：手填驱动名」）");

        if (editName != null)
        {
            NameBox.Text = editName;
            NameBox.IsEnabled = false;
            Title = "编辑/重登库 " + editName;
            ModeBanner.Visibility = Visibility.Visible;
            ModeBannerText.Text =
                "编辑/重登：以下参数已按当前网盘服务存储预填。修改授权字段后点「完成」只更新存储参数，" +
                "不会更换加密密钥，已上传文件仍可解密。驱动已锁定，避免误改后端导致库内数据丢失。";
            PresetCombo.IsEnabled = false;
            AdvancedBtn.IsEnabled = false;
        }

        Loaded += async (_, _) => await InitAsync();
    }

    private async Task InitAsync()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            await _ol.LoginAsync();
        }
        catch (Exception ex)
        {
            GuideTitle.Text = "无法连接网盘服务";
            GuideSummary.Text = ex.Message;
            return;
        }

        if (_editMode)
        {
            try
            {
                await LoadExistingAsync(_origName!);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "读取现有库配置失败：" + ex.Message, "编辑/重登",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        var preset = _existingDriver != null
            ? CloudPresets.ByDriver(_existingDriver)
            : null;
        if (preset == null && _existingDriver != null)
        {
            _manualDriver = _existingDriver;
            GuideTitle.Text = $"当前驱动：{_existingDriver}（未在内置预设中）";
            GuideSummary.Text = "按驱动元数据动态生成字段；保存时不会更换加密密钥。";
            GuideSteps.Text = "";
            GuideFailures.Text = "";
            PlatformBtn.Visibility = Visibility.Collapsed;
            _suppressPreset = true;
            PresetCombo.SelectedIndex = PresetCombo.Items.Count - 1;
            _suppressPreset = false;
            await LoadDriverAsync(_existingDriver);
        }
        else if (preset != null)
        {
            _suppressPreset = true;
            PresetCombo.SelectedIndex = Array.IndexOf(CloudPresets.All, preset);
            _suppressPreset = false;
            await ApplyPresetAsync(preset);
        }
        else
        {
            _suppressPreset = true;
            PresetCombo.SelectedIndex = 0;
            _suppressPreset = false;
            await ApplyPresetAsync(CloudPresets.All[0]);
        }

        ApplyPrefill();
    }

    private async Task LoadExistingAsync(string name)
    {
        var list = await _ol.ListStoragesAsync();
        var s = list.Content.FirstOrDefault(x => x.MountPath.TrimEnd('/') == "/" + name)
                ?? throw new InvalidOperationException($"网盘服务中未找到挂载路径 /{name}");
        _existingDriver = s.Driver;
        _existingRemark = s.Remark;
        RemarkBox.Text = s.Remark;
        _existingAddition.Clear();
        try
        {
            var raw = string.IsNullOrWhiteSpace(s.Addition) ? "{}" : s.Addition;
            var obj = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(raw);
            if (obj != null)
                foreach (var (k, v) in obj)
                    _existingAddition[k] = v.ValueKind == JsonValueKind.String
                        ? (v.GetString() ?? "")
                        : v.ToString();
        }
        catch { /* addition 非标准 JSON 时忽略，用户可重填 */ }
    }

    private async void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPreset || !_initialized && _editMode) return;
        if (PresetCombo.SelectedIndex < 0) return;

        if (PresetCombo.SelectedIndex < CloudPresets.All.Length)
        {
            await ApplyPresetAsync(CloudPresets.All[PresetCombo.SelectedIndex]);
        }
        else
        {
            _preset = null;
            GuideTitle.Text = "其它驱动（高级模式）";
            GuideSummary.Text = "点「高级：手填驱动名」，按网盘服务驱动元数据生成表单。";
            GuideSteps.Text = "";
            GuideFailures.Text = "";
            PlatformBtn.Visibility = Visibility.Collapsed;
            OAuthPanel.Visibility = Visibility.Collapsed;
            TierText.Text = "";
            FieldsPanel.Children.Clear();
            _controls.Clear();
            _info = null;
        }
    }

    private async Task ApplyPresetAsync(CloudPreset preset)
    {
        _preset = preset;
        _manualDriver = null;
        GuideTitle.Text = preset.Display;
        GuideSummary.Text = preset.Summary;
        GuideSteps.Text = FormatSteps(preset.Steps);
        GuideFailures.Text = preset.CommonFailures.Length == 0
            ? ""
            : "常见失败原因：\n" + string.Join("\n", preset.CommonFailures.Select(f => "• " + f));
        PlatformBtn.Visibility = preset.PlatformUrl.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        TierText.Text = $"档位：{preset.Tier}";
        await LoadDriverAsync(preset.Driver);
        UpdateOAuthPanel(preset);
    }

    private static string FormatSteps(string[] steps) =>
        steps.Length == 0 ? "" : "步骤：\n" + string.Join("\n", steps.Select((s, i) => $"{i + 1}. {s}"));

    private void UpdateOAuthPanel(CloudPreset preset)
    {
        if (preset.OAuth == null)
        {
            OAuthPanel.Visibility = Visibility.Collapsed;
            return;
        }
        OAuthPanel.Visibility = Visibility.Visible;
        OAuthNote.Text = preset.OAuth.Note + (preset.OAuth.Verified ? "" : "（未实测）");
        RefreshAuthUrl();
    }

    private void RefreshAuthUrl()
    {
        if (_preset?.OAuth == null) return;
        var cid = ValueOf("client_id", "ClientID", "app_id");
        var url = _preset.OAuth.AuthorizeUrl.Replace("{clientId}", Uri.EscapeDataString(cid));
        AuthUrlBox.Text = url;
        OpenAuthBtn.IsEnabled = cid.Length > 0 && !url.Contains("%7BclientId%7D") && !url.Contains("{clientId}");
    }

    private void Platform_Click(object sender, RoutedEventArgs e)
    {
        var url = _preset?.PlatformUrl ?? "";
        if (url.Length == 0) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, "打开浏览器失败：" + ex.Message); }
    }

    private void OpenAuth_Click(object sender, RoutedEventArgs e)
    {
        var url = AuthUrlBox.Text.Trim();
        if (url.Length == 0) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            OAuthResult.Text = "已打开授权页：完成授权后，把页面显示的授权码粘贴到左侧输入框。";
        }
        catch (Exception ex) { MessageBox.Show(this, "打开授权页失败：" + ex.Message); }
    }

    private async void Exchange_Click(object sender, RoutedEventArgs e)
    {
        var oauth = _preset?.OAuth;
        if (oauth == null) return;
        var code = CodeBox.Text.Trim();
        if (code.Length == 0)
        {
            MessageBox.Show(this, "请先粘贴授权码。", "授权登录", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var cid = ValueOf("client_id", "ClientID", "app_id");
        var csec = ValueOf("client_secret", "ClientSecret", "sign_key");
        if (cid.Length == 0 || csec.Length == 0)
        {
            MessageBox.Show(this, "请先填写应用 ID 与应用密钥。",
                "授权登录", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ExchangeBtn.IsEnabled = false;
        OAuthResult.Text = "正在换取访问令牌…";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            HttpResponseMessage resp;
            if (oauth.TokenFormat == "json")
            {
                var payload = JsonSerializer.Serialize(new
                {
                    grant_type = "authorization_code",
                    code,
                    client_id = cid,
                    client_secret = csec,
                    redirect_uri = oauth.RedirectUri,
                });
                resp = await http.PostAsync(oauth.TokenUrl,
                    new StringContent(payload, Encoding.UTF8, "application/json"));
            }
            else
            {
                var form = new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = code,
                    ["client_id"] = cid,
                    ["client_secret"] = csec,
                    ["redirect_uri"] = oauth.RedirectUri,
                };
                resp = await http.PostAsync(oauth.TokenUrl, new FormUrlEncodedContent(form));
            }
            var text = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                OAuthResult.Text = $"换取失败：HTTP {(int)resp.StatusCode}。{Truncate(text, 300)}";
                return;
            }
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            string? Get(params string[] names)
            {
                foreach (var n in names)
                    if (root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                        return v.GetString();
                return null;
            }
            var refresh = Get("refresh_token", "refreshToken");
            var access = Get("access_token", "accessToken");
            if (refresh != null) SetControl("refresh_token", refresh);
            if (refresh != null) SetControl("RefreshToken", refresh);
            if (access != null) SetControl("access_token", access);
            if (access != null) SetControl("AccessToken", access);
            OAuthResult.Text = refresh == null && access == null
                ? "换取响应中没有访问令牌或刷新令牌，请检查应用配置：\n" + Truncate(text, 400)
                : "已换取访问令牌并自动填入字段。请立刻离线备份刷新令牌。";
        }
        catch (Exception ex)
        {
            OAuthResult.Text = "换取失败：" + ex.Message;
        }
        finally { ExchangeBtn.IsEnabled = true; }
    }

    private static string Truncate(string s, int n) =>
        s.Length <= n ? s : s[..n] + "…";

    private void Advanced_Click(object sender, RoutedEventArgs e)
    {
        var name = PromptInput("手填驱动名",
            "输入网盘服务驱动标识（如 QuarkTV、UCTV、ThunderBrowser），将按驱动元数据生成表单：");
        if (string.IsNullOrWhiteSpace(name)) return;
        _preset = null;
        _manualDriver = name.Trim();
        _suppressPreset = true;
        PresetCombo.SelectedIndex = PresetCombo.Items.Count - 1;
        _suppressPreset = false;
        GuideTitle.Text = $"高级模式：{_manualDriver}";
        GuideSummary.Text = "按驱动元数据渲染，请按网盘服务文档填写。";
        GuideSteps.Text = "";
        GuideFailures.Text = "";
        PlatformBtn.Visibility = Visibility.Collapsed;
        OAuthPanel.Visibility = Visibility.Collapsed;
        TierText.Text = "档位：未内置";
        _ = LoadDriverAsync(_manualDriver);
    }

    private static string? PromptInput(string title, string prompt)
    {
        var dlg = new Window
        {
            Title = title, Width = 460, Height = 170,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.White,
            Foreground = Brushes.Black,
        };
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        var tb = new TextBox();
        AutomationProperties.SetAutomationId(tb, "PromptInputBox");
        panel.Children.Add(tb);
        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = "确定", Width = 70, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "取消", Width = 70 };
        btns.Children.Add(ok); btns.Children.Add(cancel);
        panel.Children.Add(btns);
        dlg.Content = panel;
        ok.Click += (_, _) => { dlg.DialogResult = true; dlg.Close(); };
        cancel.Click += (_, _) => { dlg.DialogResult = false; dlg.Close(); };
        return dlg.ShowDialog() == true ? tb.Text : null;
    }

    private async Task LoadDriverAsync(string driver)
    {
        FieldsPanel.Children.Clear();
        _controls.Clear();
        try
        {
            _info = await _ol.GetDriverInfoAsync(driver);
        }
        catch (Exception ex)
        {
            GuideTitle.Text = "获取驱动字段失败：" + driver;
            GuideSummary.Text = ex.Message;
            return;
        }
        // 只展示驱动专有字段；OpenList 公共字段（挂载路径/备注/代理策略等）由应用统一处理，避免表单噪音。
        var noise = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mount_path", "order", "remark", "webdav_policy", "down_proxy_url", "seed_policy",
        };
        var fields = _info.Common.Concat(_info.Additional)
            .Where(f => !noise.Contains(f.Name))
            .OrderByDescending(f => _preset?.FieldLabels.ContainsKey(f.Name) == true)
            .ThenByDescending(f => f.Required)
            .ToList();
        foreach (var f in fields)
            FieldsPanel.Children.Add(MakeRow(f));
        RefreshAuthUrl();
    }

    private FrameworkElement MakeRow(OlField f)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        var labelText = CloudPresets.LabelFor(_preset, f.Name);
        var helpText = CloudPresets.HelpFor(f.Help);
        var label = new TextBlock
        {
            Text = labelText + (f.Required ? " *" : "") +
                   (helpText.Length == 0 ? "" : $"  （{helpText}）"),
            Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
            TextWrapping = TextWrapping.Wrap,
        };
        panel.Children.Add(label);
        Control ctrl;
        switch (f.Type)
        {
            case "select":
                var cb = new ComboBox { Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
                AutomationProperties.SetAutomationId(cb, "field_" + f.Name);
                // 界面显示中文，Tag 保留提交给网盘服务的原始选项值
                foreach (var o in f.OptionList)
                    cb.Items.Add(new ComboBoxItem { Content = CloudPresets.OptionLabel(o), Tag = o });
                SelectComboByValue(cb, f.Default);
                ctrl = cb;
                break;
            case "bool":
                panel.Children.Remove(label);
                var chk = new CheckBox { Content = labelText, IsChecked = f.Default == "true" || f.Default == "t" };
                AutomationProperties.SetAutomationId(chk, "field_" + f.Name);
                panel.Children.Add(chk);
                _controls[f.Name] = chk;
                return panel;
            case "text":
                var tb = new TextBox
                {
                    AcceptsReturn = true, Height = 60, TextWrapping = TextWrapping.Wrap,
                    Text = f.Default ?? "", Background = Brushes.White,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
                };
                AutomationProperties.SetAutomationId(tb, "field_" + f.Name);
                ctrl = tb;
                break;
            case "number":
            case "string":
            default:
                var t = new TextBox
                {
                    Text = f.Default ?? "",
                    Background = Brushes.White,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
                };
                // 令牌/密钥类字段建议横向占满，便于粘贴
                if (f.Name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                    f.Name.Contains("cookie", StringComparison.OrdinalIgnoreCase) ||
                    f.Name.Contains("secret", StringComparison.OrdinalIgnoreCase))
                    t.Width = 560;
                AutomationProperties.SetAutomationId(t, "field_" + f.Name);
                ctrl = t;
                break;
        }
        _controls[f.Name] = ctrl;
        panel.Children.Add(ctrl);
        return panel;
    }

    private void ApplyPrefill()
    {
        foreach (var (k, v) in _existingAddition)
        {
            if (_controls.ContainsKey(k)) SetControl(k, v);
        }
    }

    private string ValueOf(params string[] names)
    {
        foreach (var n in names)
        {
            if (_controls.TryGetValue(n, out var c))
            {
                var v = ReadControl(c);
                if (v.Length > 0) return v;
            }
        }
        return "";
    }

    private void SetControl(string fieldName, string value)
    {
        if (_controls.TryGetValue(fieldName, out var c)) SetControl(c, value);
    }

    private static void SetControl(Control c, string value)
    {
        switch (c)
        {
            case TextBox tb: tb.Text = value; break;
            case ComboBox cb:
                SelectComboByValue(cb, value);
                break;
            case CheckBox chk:
                chk.IsChecked = value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                                value.Equals("t", StringComparison.OrdinalIgnoreCase);
                break;
        }
    }

    /// <summary>按原始选项值选中下拉项；值不在列表中时补一项（显示原值）。</summary>
    private static void SelectComboByValue(ComboBox cb, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            if (cb.Items.Count > 0) cb.SelectedIndex = 0;
            return;
        }
        foreach (var item in cb.Items)
        {
            if (item is ComboBoxItem ci && string.Equals(ci.Tag as string, value, StringComparison.OrdinalIgnoreCase))
            {
                cb.SelectedItem = item;
                return;
            }
        }
        var extra = new ComboBoxItem { Content = value, Tag = value };
        cb.Items.Add(extra);
        cb.SelectedItem = extra;
    }

    private Dictionary<string, object> CollectAddition()
    {
        var d = new Dictionary<string, object>();
        if (_info == null) return d;
        foreach (var f in _info.Additional)
        {
            if (!_controls.TryGetValue(f.Name, out var c)) continue;
            if (f.Type == "bool" && c is CheckBox chk)
            {
                // OpenList 驱动按原生 JSON 布尔解析（字符串 "t"/"f" 会被拒绝）
                d[f.Name] = chk.IsChecked == true;
                continue;
            }
            var val = ReadControl(c);
            if (val.Length == 0) continue;
            if (f.Type == "number" && long.TryParse(val, out var n)) d[f.Name] = n;
            else d[f.Name] = val;
        }
        return d;
    }

    private List<string> MissingRequired()
    {
        var missing = new List<string>();
        if (_info == null) return missing;
        foreach (var f in _info.Additional.Where(x => x.Required))
        {
            if (!_controls.TryGetValue(f.Name, out var c)) continue;
            if (ReadControl(c).Length == 0)
                missing.Add(CloudPresets.LabelFor(_preset, f.Name));
        }
        return missing;
    }

    private static string ReadControl(Control c) => c switch
    {
        TextBox tb => tb.Text.Trim(),
        // 下拉项显示中文，原始选项值存在 Tag 里
        ComboBox cb => (cb.SelectedItem as ComboBoxItem)?.Tag as string ?? cb.SelectedItem?.ToString() ?? "",
        // OpenList 的布尔字段用 t/f（strconv 风格），传 true/false 会被驱动拒绝
        CheckBox chk => chk.IsChecked == true ? "t" : "f",
        _ => "",
    };

    private string? CurrentDriver()
    {
        if (_preset != null) return _preset.Driver;
        if (!string.IsNullOrEmpty(_manualDriver)) return _manualDriver;
        return _info?.Config?.Name;
    }

    private async Task<(bool Ok, string Message)> ProbeAsync(string mountPath)
    {
        var (u, p) = OpenListClient.LoadAdminCredentials();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        var req = new HttpRequestMessage(new HttpMethod("PROPFIND"),
            $"http://127.0.0.1:{AppSettings.Current.OpenListPort}/dav{mountPath}/");
        req.Headers.Add("Authorization",
            "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes($"{u}:{p}")));
        req.Headers.Add("Depth", "0");
        try
        {
            using var resp = await http.SendAsync(req);
            if (resp.StatusCode == System.Net.HttpStatusCode.MultiStatus)
                return (true, "连接测试通过：网盘服务可访问该库根目录。");
            return (false, $"网盘服务返回 HTTP {(int)resp.StatusCode}；请检查授权字段是否有效。");
        }
        catch (Exception ex)
        {
            return (false, "连接测试失败：" + ex.Message);
        }
    }

    /// <summary>连接测试：编辑模式直接更新现有存储；新增模式只建临时 OpenList 存储（不生成 crypt 密钥）。</summary>
    private async Task EnsureStorageForTestAsync(string name, string driver, Dictionary<string, object> addition)
    {
        if (_editMode)
        {
            await _mgr.UpdateStorageAsync(name, driver, addition, RemarkBox.Text.Trim());
            return;
        }
        try { await DeleteStorageByNameAsync(name); } catch { }
        await _ol.CreateStorageAsync(new OlStorage
        {
            MountPath = "/" + name,
            Driver = driver,
            Remark = RemarkBox.Text.Trim(),
            Addition = JsonSerializer.Serialize(addition),
            WebDavPolicy = "native_proxy",
            SeedPolicy = "inherit",
        });
    }

    private async Task DeleteStorageByNameAsync(string name)
    {
        var list = await _ol.ListStoragesAsync();
        var s = list.Content.FirstOrDefault(x => x.MountPath.TrimEnd('/') == "/" + name);
        if (s != null) await _ol.DeleteStorageAsync(s.Id);
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var driver = CurrentDriver();
        if (string.IsNullOrEmpty(name)) { MessageBox.Show(this, "请填写库名"); return; }
        if (string.IsNullOrEmpty(driver)) { MessageBox.Show(this, "请选择网盘/驱动"); return; }
        if (_info == null) { MessageBox.Show(this, "驱动字段尚未加载完成，请稍候再试"); return; }
        var missing = MissingRequired();
        if (missing.Count > 0)
        {
            MessageBox.Show(this, "以下必填字段仍为空：\n" + string.Join("\n", missing),
                "连接测试", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        TestBtn.IsEnabled = false;
        TestResultText.Text = "测试中…";
        try
        {
            await _ol.LoginAsync();
            await EnsureStorageForTestAsync(name, driver, CollectAddition());
            var (ok, msg) = await ProbeAsync("/" + name);
            TestResultText.Text = msg;
            MessageBox.Show(this, msg, "连接测试", MessageBoxButton.OK,
                ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            TestResultText.Text = "测试失败：" + ex.Message;
            MessageBox.Show(this, ex.Message, "测试失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { TestBtn.IsEnabled = true; }
    }

    private async void Ok_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) { MessageBox.Show(this, "请填写库名"); return; }
        var driver = CurrentDriver();
        if (string.IsNullOrEmpty(driver)) { MessageBox.Show(this, "请选择网盘/驱动"); return; }
        if (_info == null) { MessageBox.Show(this, "驱动字段尚未加载完成，请稍候再试"); return; }
        var missing = MissingRequired();
        if (missing.Count > 0)
        {
            MessageBox.Show(this, "以下必填字段仍为空：\n" + string.Join("\n", missing),
                "保存", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        OkBtn.IsEnabled = false;
        TestResultText.Text = "保存中…";
        try
        {
            if (_editMode)
            {
                await _mgr.UpdateStorageAsync(name, driver, CollectAddition(), RemarkBox.Text.Trim());
                var (ok, msg) = await ProbeAsync("/" + name);
                TestResultText.Text = msg;
                if (!ok)
                {
                    MessageBox.Show(this,
                        "参数已保存，但连接测试未通过：\n" + msg +
                        "\n\n库管理页稍后会标记为「凭证过期」，可再次点「编辑/重登」修正。",
                        "已保存（测试未通过）", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            else
            {
                // 「连接测试」会先建一个临时 OpenList 存储；若直接 AddLibraryAsync 会撞
                // UNIQUE constraint failed: x_storages.mount_path。先清理临时存储，
                // 但真正挂载过的同名库必须拒绝，避免误删已有库。
                var list = await _ol.ListStoragesAsync();
                if (list.Content.Any(x => x.MountPath.TrimEnd('/') == "/" + name))
                {
                    if (_mgr.HasCryptKey(name))
                        throw new InvalidOperationException(
                            $"库名 {name} 已存在（已有加密密钥）。请换一个库名，或用库管理页的「编辑/重登」。");
                    await DeleteStorageByNameAsync(name);
                }
                await _mgr.AddLibraryAsync(name, driver, CollectAddition(), RemarkBox.Text.Trim(), _svc,
                    _preset?.ChunkSize ?? "512M");
                TestResultText.Text = "已添加并挂载。";
            }
            LibraryName = name;
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            TestResultText.Text = "保存失败。";
            MessageBox.Show(this, "保存失败：" + ex.Message, "AiPanBox",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { OkBtn.IsEnabled = true; }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
