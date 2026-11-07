using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using NetBoost.Core;
using NetBoost.Models;

namespace NetBoost;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ServiceDefinition> _services = new();
    private readonly ObservableCollection<DomainResult> _rows = new();
    private readonly ObservableCollection<DnsEntry> _dnsRows = new();
    private readonly ObservableCollection<DnsServer> _dnsPresets = new();
    private readonly Dictionary<string, ProbeResult> _beforeProbes = new();
    private readonly Dictionary<string, ProbeResult> _afterProbes = new();
    private readonly Dictionary<string, double> _phaseSeconds = new();

    private List<DomainResult> _lastResults = new();
    private IReadOnlyList<AdapterDnsInfo> _dnsAdapters = Array.Empty<AdapterDnsInfo>();
    private AdapterDnsInfo? _dnsAdapter;
    private DateTime _adaptersDetectedAt = DateTime.MinValue;
    private bool _suppressAdapterChange;
    private bool _busy;
    private int _totalUnits = 1;
    private int _doneUnits;
    private double _pipelineSeconds;
    private DispatcherTimer? _presetConfirmTimer;

    public MainWindow()
    {
        InitializeComponent();

        foreach (var service in ServiceCatalog.All) _services.Add(service);
        foreach (var preset in DnsServerCatalog.Candidates) _dnsPresets.Add(preset);

        ServiceList.ItemsSource = _services;
        ResultsGrid.ItemsSource = _rows;
        DnsGrid.ItemsSource = _dnsRows;
        PresetList.ItemsSource = _dnsPresets;

        Loaded += OnWindowLoaded;
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        WindowTheme.ApplyDarkCaption(this);

        HostsPathText.Text = $"写入目标：{HostsManager.HostsPath}";
        DnsProbeHint.Text = $"测试域名 {string.Join(" / ", DnsBenchmark.DefaultDomains)} 共 {DnsBenchmark.DefaultDomains.Count} 个，" +
                            "主/备地址各测一遍取平均；每个域名首次单发，没回音再补测一次";

        if (HostsManager.IsElevated)
        {
            AdminBadge.Text = "管理员权限";
            AdminBadge.Foreground = Brush("#4ADE80");
        }
        else
        {
            AdminBadge.Text = "普通权限 · 写入时自动请求管理员";
            AdminBadge.Foreground = Brush("#F5C451");
            SetStatus("提示：检测与测速无需管理员权限；点击「应用」写入 hosts / DNS 时会弹出系统管理员授权。");
        }

        var foreign = HostsManager.DetectForeignManager();
        if (foreign is not null)
            SetStatus($"提示：当前 hosts 由 {foreign} 接管，它下次同步可能覆盖 NetBoost 的优化结果。");

        RefreshCurrentDns(force: true);

        UpdateIpTableText();
        _ = AutoRefreshIpTableAsync();
    }

    private void UpdateIpTableText()
    {
        IpTableText.Text = $"应急兜底 IP 表：{KnownIpStore.Describe()} · 仅当 DNS 解析或连接失败时启用";
    }

    private async Task AutoRefreshIpTableAsync()
    {
        try
        {
            var updated = await KnownIpStore.RefreshAsync(force: false).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() =>
            {
                UpdateIpTableText();
                if (updated && !_busy)
                    SetStatus("兜底 IP 表已在线更新：" + KnownIpStore.Describe());
            });
        }
        catch
        {
            // 在线刷新失败就继续用当前表
        }
    }

    private async void BtnRefreshIps_Click(object sender, RoutedEventArgs e)
    {
        BtnRefreshIps.IsEnabled = false;
        SetStatus("正在尝试在线更新兜底 IP 表…");
        try
        {
            var updated = await KnownIpStore.RefreshAsync(force: true);
            UpdateIpTableText();
            SetStatus(updated
                ? "兜底 IP 表已更新：" + KnownIpStore.Describe()
                : "在线更新失败，继续使用当前表：" + KnownIpStore.Describe());
        }
        finally
        {
            BtnRefreshIps.IsEnabled = true;
        }
    }

    private static SolidColorBrush Brush(string hex) =>
        new((Color)ColorConverter.ConvertFromString(hex));

    private void MainTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // 仅在页签切换后刷新一次当前 DNS 状态；2 秒内不重复枚举网卡，避免频繁卡顿
        if (!IsLoaded) return;
        if ((DateTime.Now - _adaptersDetectedAt).TotalSeconds < 2) return;
        RefreshCurrentDns();
    }

    // ---------------------------------------------------------------- 主流程

    private async void BtnOptimize_Click(object sender, RoutedEventArgs e) => await RunPipelineAsync(true);

    private async void BtnDetect_Click(object sender, RoutedEventArgs e) => await RunPipelineAsync(false);

    private async void BtnForceRefresh_Click(object sender, RoutedEventArgs e) =>
        await RunPipelineAsync(false, forceRefresh: true);

    private async Task RunPipelineAsync(bool apply, bool forceRefresh = false)
    {
        if (_busy) return;

        var selected = _services.Where(s => s.IsEnabled).ToList();
        if (selected.Count == 0)
        {
            SetStatus("请至少勾选一个服务");
            return;
        }

        if (forceRefresh) ResultCache.Clear();

        SetBusy(true);
        ResetResults();
        _phaseSeconds.Clear();
        TimingText.Text = "";

        var totalDomains = selected.Sum(s => s.Domains.Length);
        StartProgress(selected.Count * 2 + totalDomains);
        var wall = Stopwatch.StartNew();

        try
        {
            // 优化前测速与域名检测互不依赖，一起并发跑，总耗时取决于较慢的那一条
            SetStatus($"正在并发执行：优化前测速（{selected.Count} 个服务）+ 域名检测（{totalDomains} 个域名）…");
            var swBefore = Stopwatch.StartNew();
            var swDetect = Stopwatch.StartNew();

            var beforeTasks = selected.Select(async service =>
            {
                var probe = await HttpProbe.RunBestOfAsync(service.ProbeUrl).ConfigureAwait(false);
                await Dispatcher.InvokeAsync(() =>
                {
                    _beforeProbes[service.Id] = probe;
                    AdvanceProgress();
                });
            }).ToList();

            var targets = new List<(ServiceDefinition Service, string Host, int Order)>();
            var index = 0;
            foreach (var service in selected)
                foreach (var host in service.Domains)
                    targets.Add((service, host, index++));

            var results = new List<DomainResult>();
            using var gate = new SemaphoreSlim(6);

            var detectTasks = targets.Select(async target =>
            {
                DomainResult result;
                await gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    result = await Detector.DetectOneAsync(
                        target.Service.Id, target.Service.Name, target.Host, target.Order,
                        CancellationToken.None, forceRefresh).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    result = new DomainResult
                    {
                        ServiceId = target.Service.Id,
                        Service = target.Service.Name,
                        Host = target.Host,
                        Order = target.Order,
                        Note = "检测异常：" + ex.Message
                    };
                }
                finally
                {
                    gate.Release();
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    results.Add(result);
                    AdvanceProgress();
                });
            }).ToList();

            await Task.WhenAll(beforeTasks.Concat(detectTasks));
            RecordPhase("优化前测速", swBefore);
            RecordPhase("域名检测", swDetect);

            results.Sort((a, b) => a.Order.CompareTo(b.Order));
            _lastResults = results;

            // 默认勾选所有可优化项，用户可逐条取消
            foreach (var result in results) result.Selected = result.Applicable;

            foreach (var row in results) _rows.Add(row);

            BtnApply.IsEnabled = results.Any(r => r.Applicable);
            UpdateStats(withProbe: false);

            if (apply)
                await ApplyAndVerifyAsync(results);
            else
            {
                var fallbackCount = results.Count(r => r.UsedFallback);
                SetStatus(fallbackCount > 0
                    ? $"检测完成，未修改 hosts。注意：{fallbackCount} 个域名使用了应急兜底 IP，不代表网络已恢复。"
                    : "检测完成，未修改 hosts。可点「应用结果」写入优化。");
                SetProgress(_totalUnits, _totalUnits);
            }
        }
        catch (Exception ex)
        {
            SetStatus("执行失败：" + ex.Message);
        }
        finally
        {
            _pipelineSeconds = wall.Elapsed.TotalSeconds;
            ShowTimings();
            SetBusy(false);
        }
    }

    // ---------------------------------------------------------------- 应用与复测

    private async Task ApplyAndVerifyAsync(List<DomainResult> results)
    {
        var applicable = results.Where(r => r.Applicable && r.Selected).ToList();
        if (applicable.Count == 0)
        {
            SetStatus("没有勾选可优化项，hosts 保持不变");
            UpdateStats(withProbe: false);
            return;
        }

        var maps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in applicable) maps[row.Host] = row.NewIp;

        var sw = Stopwatch.StartNew();
        try
        {
            SetStatus("正在备份并写入 hosts…");
            var outcome = Elevation.ApplyHosts(maps);
            HostsManager.FlushDns();

            var message = $"已写入 {outcome.Written} 条优化记录";
            if (outcome.RemovedConflicts > 0)
                message += $"，并清理了 {outcome.RemovedConflicts} 条冲突的旧条目";
            message += $"（备份：{Path.GetFileName(outcome.BackupPath)}）";
            SetStatus(message);
        }
        catch (Exception ex)
        {
            SetStatus("写入 hosts 失败：" + ex.Message);
            return;
        }
        RecordPhase("写入 hosts", sw);

        await Task.Delay(400).ConfigureAwait(true);

        // 阶段三：优化后复测（并发）
        var enabled = _services.Where(s => s.IsEnabled).ToList();
        StartProgress(enabled.Count);

        SetStatus($"正在复测优化后速度（{enabled.Count} 个服务，并发进行）…");
        var swAfter = Stopwatch.StartNew();

        var afterTasks = enabled.Select(async service =>
        {
            var probe = await HttpProbe.RunBestOfAsync(service.ProbeUrl).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() =>
            {
                _afterProbes[service.Id] = probe;
                AdvanceProgress();
            });
        }).ToList();

        await Task.WhenAll(afterTasks);
        RecordPhase("优化后复测", swAfter);

        UpdateStats(withProbe: true);
        SetProgress(_totalUnits, _totalUnits);
        SetStatus(BuildSummary(applicable.Count));
    }

    private async void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _lastResults.Count == 0) return;

        SetBusy(true);
        try
        {
            await ApplyAndVerifyAsync(_lastResults);
        }
        catch (Exception ex)
        {
            SetStatus("应用失败：" + ex.Message);
        }
        finally
        {
            ShowTimings();
            SetBusy(false);
        }
    }

    // ---------------------------------------------------------------- 还原 / 查看

    private void BtnRestore_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var latest = HostsManager.LatestBackup();
        if (latest is null && !File.Exists(HostsManager.HostsPath))
        {
            SetStatus("没有找到可还原的备份");
            return;
        }

        var message = latest is null
            ? "未找到备份，将仅移除 NetBoost 托管块。继续吗？"
            : $"将把 hosts 还原为备份：\n{Path.GetFileName(latest)}\n\n继续吗？";

        if (!Elevation.IsElevated)
            message += "\n\n接下来会弹出系统管理员授权（UAC）。";

        if (MessageBox.Show(message, "还原 hosts", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes)
            return;

        try
        {
            var result = Elevation.RestoreHosts(latest is null ? null : Path.GetFileName(latest));
            HostsManager.FlushDns();
            SetStatus(result);
        }
        catch (Exception ex)
        {
            SetStatus("还原失败：" + ex.Message);
        }
    }

    private void BtnOpenHosts_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("notepad.exe", HostsManager.HostsPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus("无法打开 hosts：" + ex.Message);
        }
    }

    // ---------------------------------------------------------------- DNS

    private async void BtnTestDns_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        SetBusy(true);
        try
        {
            _dnsRows.Clear();
            DnsSummaryText.Text = "";
            BtnApplyDns.IsEnabled = false;
            foreach (var preset in _dnsPresets) preset.MeasuredText = "";

            RefreshCurrentDns(force: true);

            var items = new List<(string Name, string Primary, string Secondary, string Note)>();
            if (_dnsAdapter is not null && _dnsAdapter.Servers.Length > 0)
            {
                items.Add((
                    "当前系统 DNS",
                    _dnsAdapter.Servers[0],
                    _dnsAdapter.Servers.Length > 1 ? _dnsAdapter.Servers[1] : "",
                    "来自网卡现有配置"));
            }
            foreach (var server in DnsServerCatalog.Candidates)
                items.Add((server.Name, server.Primary, server.Secondary, server.Note));

            StartProgress(items.Count);
            var domains = DnsBenchmark.DefaultDomains;
            SetStatus($"正在测试 {items.Count} 个 DNS（主+备选，{domains.Count} 个域名取平均，并发 5）…");

            var rows = new List<DnsEntry>();
            using var gate = new SemaphoreSlim(5);

            var tasks = items.Select(async item =>
            {
                DnsPairOutcome outcome;
                await gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    outcome = await DnsBenchmark.ProbePairAsync(item.Primary, item.Secondary, domains)
                        .ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }

                var entry = new DnsEntry
                {
                    Name = item.Name,
                    Primary = item.Primary,
                    Secondary = item.Secondary,
                    Note = item.Note,
                    IsCurrent = item.Name == "当前系统 DNS",
                    Responded = outcome.Responded,
                    LatencyMs = outcome.PrimaryLatencyMs,
                    SecondaryLatencyMs = outcome.SecondaryLatencyMs,
                    AnsweredDomains = outcome.PrimaryAnswered,
                    DomainCount = outcome.DomainCount,
                    RCode = outcome.RCode,
                    AnswerCount = outcome.AnswerCount,
                    AnswerText = outcome.FirstAnswer
                };

                await Dispatcher.InvokeAsync(() =>
                {
                    rows.Add(entry);
                    _dnsRows.Add(entry);
                    AdvanceProgress();
                });
            }).ToList();

            await Task.WhenAll(tasks);

            var usable = rows.Where(r => r.Responded).OrderBy(r => r.LatencyMs ?? double.MaxValue).ToList();
            if (usable.Count > 0) usable[0].IsBest = true;

            DnsGrid.Items.Refresh();
            BtnApplyDns.IsEnabled = usable.Count > 0;
            SetProgress(_totalUnits, _totalUnits);

            // 把实测延迟回填到预设卡片副标题，选谁一眼有据
            foreach (var preset in _dnsPresets)
            {
                var match = rows.FirstOrDefault(r =>
                    string.Equals(r.Primary, preset.Primary, StringComparison.Ordinal));
                preset.MeasuredText = match is null
                    ? ""
                    : match.Responded && match.LatencyMs.HasValue
                        ? $"实测 {match.LatencyMs.Value:F0} ms"
                        : "实测无响应";
            }

            DnsSummaryText.Text = usable.Count == 0
                ? $"{rows.Count} 个 DNS 全部无响应"
                : $"可用 {usable.Count} / {rows.Count} · 最快 {usable[0].Name} {usable[0].LatencyText} · {domains.Count} 域名平均";

            SetStatus(usable.Count == 0
                ? "所有 DNS 均无响应，请检查网络"
                : $"DNS 测试完成，最快为 {usable[0].Name}（{usable[0].LatencyText}）。选中一行后可一键设为优先 DNS。");
        }
        catch (Exception ex)
        {
            SetStatus("DNS 测试失败：" + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void BtnApplyDns_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var entry = DnsGrid.SelectedItem as DnsEntry;
        if (entry is null)
        {
            SetStatus("请先在列表中选中一行（也可直接点该行内的「应用」按钮）");
            return;
        }

        ApplyDnsPair(entry.Primary, entry.Secondary, entry.Name);
    }

    /// <summary>表格行内的「应用」按钮：明确指定这一行，不依赖表格当前选中项。</summary>
    private void DnsRowApply_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DnsEntry entry }) return;
        ApplyDnsPair(entry.Primary, entry.Secondary, entry.Name);
    }

    private void BtnExportDns_Click(object sender, RoutedEventArgs e)
    {
        if (_dnsRows.Count == 0)
        {
            SetStatus("还没有测速结果，先点「测试全部 DNS」");
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出 DNS 测速结果",
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = $"dns-{DateTime.Now:yyyyMMdd-HHmm}.csv"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("DNS 服务,主 DNS,备选 DNS,延迟(ms),备选延迟(ms),状态,解析结果,说明");
            foreach (var row in _dnsRows)
            {
                sb.AppendLine(string.Join(",", new[]
                {
                    CsvCell(row.Name),
                    row.Primary,
                    row.Secondary,
                    row.Responded && row.LatencyMs.HasValue ? $"{row.LatencyMs.Value:F0}" : "",
                    row.SecondaryLatencyMs.HasValue ? $"{row.SecondaryLatencyMs.Value:F0}" : "",
                    CsvCell(row.StatusText),
                    CsvCell(row.AnswerText),
                    CsvCell(row.NoteText)
                }));
            }

            // 带 BOM，Excel 直接双击不乱码
            File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(true));
            SetStatus($"已导出 {_dnsRows.Count} 条结果：{dialog.FileName}");
        }
        catch (Exception ex)
        {
            SetStatus("导出失败：" + ex.Message);
        }
    }

    private static string CsvCell(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;

    /// <summary>点常用组合卡片：第一次进入确认态，4 秒内再点一次才写入（不再每次都弹 MessageBox）。</summary>
    private void PresetApply_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DnsServer preset }) return;

        if (!preset.Pending)
        {
            foreach (var item in _dnsPresets) item.Pending = false;
            preset.Pending = true;

            _presetConfirmTimer ??= CreatePresetConfirmTimer();
            _presetConfirmTimer.Stop();
            _presetConfirmTimer.Start();

            var adapterName = _dnsAdapter?.Name ?? "未检测到网卡";
            var ipv6Note = ChkIpv6.IsChecked == true ? "（含 IPv6）" : "";
            SetStatus($"将修改网卡「{adapterName}」{ipv6Note}：{preset.Name} {preset.PairText}；4 秒内再点一次卡片确认");
            return;
        }

        preset.Pending = false;
        _presetConfirmTimer?.Stop();
        ApplyDnsPair(preset.Primary, preset.Secondary, preset.Name, skipConfirm: true);
    }

    private DispatcherTimer CreatePresetConfirmTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            foreach (var item in _dnsPresets) item.Pending = false;
        };
        return timer;
    }

    /// <summary>把当前网卡的主/备（IPv4 + IPv6）填回输入框，方便微调。</summary>
    private void BtnFillCurrentDns_Click(object sender, RoutedEventArgs e)
    {
        if (_dnsAdapter is null)
        {
            SetStatus("未检测到可用网卡");
            return;
        }

        var servers = _dnsAdapter.Servers;
        TxtPrimaryDns.Text = servers.Length > 0 ? servers[0] : "";
        TxtSecondaryDns.Text = servers.Length > 1 ? servers[1] : "";
        TxtIpv6Primary.Text = _dnsAdapter.Ipv6Servers.Length > 0 ? _dnsAdapter.Ipv6Servers[0] : "";
        TxtIpv6Secondary.Text = _dnsAdapter.Ipv6Servers.Length > 1 ? _dnsAdapter.Ipv6Servers[1] : "";
        SetStatus($"已填入网卡「{_dnsAdapter.Name}」当前 DNS");
    }

    /// <summary>手动填主/备地址后应用。</summary>
    private void BtnApplyManualDns_Click(object sender, RoutedEventArgs e)
    {
        var primary = TxtPrimaryDns.Text.Trim();
        var secondary = TxtSecondaryDns.Text.Trim();

        if (primary.Length == 0)
        {
            SetStatus("请先填写主 DNS 地址");
            return;
        }

        ApplyDnsPair(primary, secondary, "手动设置");
    }

    /// <summary>
    /// 统一的「设置主/备 DNS」入口：预设卡片、表格行、手动输入都走这里，
    /// 保证确认、备份、刷新缓存的流程完全一致。
    /// </summary>
    private bool ApplyDnsPair(string primary, string secondary, string label, bool skipConfirm = false)
    {
        if (_busy) return false;

        primary = (primary ?? "").Trim();
        secondary = (secondary ?? "").Trim();

        if (!IsIpv4Address(primary))
        {
            SetStatus($"主 DNS 地址无效：{primary}，请填写 IPv4 地址");
            return false;
        }
        if (secondary.Length > 0 && !IsIpv4Address(secondary))
        {
            SetStatus($"备选 DNS 地址无效：{secondary}，请填写 IPv4 地址");
            return false;
        }

        var adapter = _dnsAdapter;
        if (adapter is null || adapter.Index < 0)
        {
            SetStatus("未检测到可用网卡，无法修改 DNS");
            return false;
        }

        var handleIpv6 = ChkIpv6.IsChecked == true;
        var ipv6Primary = handleIpv6 ? TxtIpv6Primary.Text.Trim() : "";
        var ipv6Secondary = handleIpv6 ? TxtIpv6Secondary.Text.Trim() : "";

        if (ipv6Primary.Length > 0 && !IsIpv6Address(ipv6Primary))
        {
            SetStatus($"IPv6 主 DNS 地址无效：{ipv6Primary}");
            return false;
        }
        if (ipv6Secondary.Length > 0 && !IsIpv6Address(ipv6Secondary))
        {
            SetStatus($"IPv6 备选 DNS 地址无效：{ipv6Secondary}");
            return false;
        }

        var pairText = secondary.Length > 0
            ? $"主 {primary}\n备 {secondary}"
            : $"主 {primary}\n（不设置备选）";

        string ipv6Text;
        if (!handleIpv6) ipv6Text = "IPv6 DNS 保持不变";
        else if (ipv6Primary.Length == 0) ipv6Text = "IPv6 DNS 将清空为自动获取";
        else ipv6Text = $"IPv6 主 {ipv6Primary}" + (ipv6Secondary.Length > 0 ? $" / 备 {ipv6Secondary}" : "");

        var confirm = $"将网卡「{adapter.Name}」的 DNS 设置为：\n{pairText}\n\n" +
                      $"{ipv6Text}\n来源：{label}\n原配置会自动保存，之后可点「还原 DNS」恢复。继续吗？";

        if (!Elevation.IsElevated)
            confirm += "\n\n点击「是」后会弹出系统管理员授权（UAC）。";

        if (!skipConfirm &&
            MessageBox.Show(confirm, "设置 DNS", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes)
            return false;

        try
        {
            var result = Elevation.ApplyDns(adapter, primary, secondary,
                ipv6Primary, ipv6Secondary, handleIpv6);
            RefreshCurrentDns(force: true);
            SetStatus($"{result}；已刷新 DNS 缓存");
            return true;
        }
        catch (Exception ex)
        {
            SetStatus("设置 DNS 失败：" + ex.Message);
            return false;
        }
    }

    private static bool IsIpv4Address(string value) =>
        System.Net.IPAddress.TryParse(value, out var address) &&
        address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;

    private static bool IsIpv6Address(string value) =>
        System.Net.IPAddress.TryParse(value, out var address) &&
        address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;

    private void BtnRestoreDns_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var message = DnsConfigurator.HasBackup()
            ? "将把 DNS 还原为修改前的配置。继续吗？"
            : "没有找到备份，将把 DNS 恢复为自动获取（DHCP）。继续吗？";

        if (!Elevation.IsElevated)
            message += "\n\n接下来会弹出系统管理员授权（UAC）。";

        if (MessageBox.Show(message, "还原 DNS", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes)
            return;

        try
        {
            var result = Elevation.RestoreDns();
            RefreshCurrentDns(force: true);
            SetStatus(result);
        }
        catch (Exception ex)
        {
            SetStatus("还原 DNS 失败：" + ex.Message);
        }
    }

    /// <summary>刷新网卡列表与当前主/备（IPv4 + IPv6）展示。force 为 true 时忽略 2 秒节流。</summary>
    private void RefreshCurrentDns(bool force = false)
    {
        if (force || _dnsAdapters.Count == 0 ||
            (DateTime.Now - _adaptersDetectedAt).TotalSeconds >= 2)
        {
            _dnsAdapters = DnsConfigurator.DetectAll();
            _adaptersDetectedAt = DateTime.Now;
        }

        var selectedId = _dnsAdapter?.AdapterId;
        _dnsAdapter = _dnsAdapters.FirstOrDefault(a =>
                          string.Equals(a.AdapterId, selectedId, StringComparison.OrdinalIgnoreCase))
                      ?? _dnsAdapters.FirstOrDefault();

        _suppressAdapterChange = true;
        CmbAdapter.Items.Clear();
        foreach (var adapter in _dnsAdapters)
            CmbAdapter.Items.Add($"{adapter.Name}  ·  {(adapter.Servers.Length == 0 ? "自动获取" : string.Join(" , ", adapter.Servers))}");
        if (_dnsAdapter is not null)
            CmbAdapter.SelectedIndex = _dnsAdapters.ToList().FindIndex(a =>
                string.Equals(a.AdapterId, _dnsAdapter.AdapterId, StringComparison.OrdinalIgnoreCase));
        CmbAdapter.IsEnabled = _dnsAdapters.Count > 1;
        _suppressAdapterChange = false;

        if (_dnsAdapter is null)
        {
            CurrentDnsText.Text = "未检测到活动网卡";
            CurrentDns6Text.Text = "";
            CurrentDnsMatchText.Text = "";
            TxtPrimaryDns.Text = "";
            TxtSecondaryDns.Text = "";
            TxtIpv6Primary.Text = "";
            TxtIpv6Secondary.Text = "";
            foreach (var preset in _dnsPresets) preset.IsCurrent = false;
            return;
        }

        var servers = _dnsAdapter.Servers;
        var primary = servers.Length > 0 ? servers[0] : "";
        var secondary = servers.Length > 1 ? servers[1] : "";

        CurrentDnsText.Text = servers.Length == 0
            ? $"{_dnsAdapter.Name}  ·  IPv4 自动获取（DHCP）"
            : $"{_dnsAdapter.Name}  ·  IPv4 {string.Join(" , ", servers)}";

        CurrentDns6Text.Text = _dnsAdapter.Ipv6Servers.Length == 0
            ? "IPv6 自动获取（DHCP）"
            : $"IPv6 {string.Join(" , ", _dnsAdapter.Ipv6Servers)}";

        // 预填手动输入框，方便用户在当前值上微调
        TxtPrimaryDns.Text = primary;
        TxtSecondaryDns.Text = secondary;
        TxtIpv6Primary.Text = _dnsAdapter.Ipv6Servers.Length > 0 ? _dnsAdapter.Ipv6Servers[0] : "";
        TxtIpv6Secondary.Text = _dnsAdapter.Ipv6Servers.Length > 1 ? _dnsAdapter.Ipv6Servers[1] : "";

        // 找出与当前主/备完全一致的预设，卡片高亮 + 右侧文字标注
        var match = _dnsPresets.FirstOrDefault(p =>
            p.Primary == primary && p.Secondary == secondary);

        foreach (var preset in _dnsPresets)
            preset.IsCurrent = ReferenceEquals(preset, match);

        if (match is not null)
            CurrentDnsMatchText.Text = $"与「{match.Name}」组合一致";
        else if (servers.Length > 0)
            CurrentDnsMatchText.Text = "自定义组合";
        else
            CurrentDnsMatchText.Text = "未指定组合";
    }

    private void CmbAdapter_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressAdapterChange) return;
        var index = CmbAdapter.SelectedIndex;
        if (index < 0 || index >= _dnsAdapters.Count) return;
        _dnsAdapter = _dnsAdapters[index];
        RefreshCurrentDns();
    }

    private void ChkIpv6_Checked(object sender, RoutedEventArgs e)
    {
        var enabled = ChkIpv6.IsChecked == true;
        if (TxtIpv6Primary is null || TxtIpv6Secondary is null) return;
        TxtIpv6Primary.IsEnabled = enabled;
        TxtIpv6Secondary.IsEnabled = enabled;
    }

    // ---------------------------------------------------------------- 界面辅助

    private void ResetResults()
    {
        _rows.Clear();
        _beforeProbes.Clear();
        _afterProbes.Clear();
        _lastResults = new List<DomainResult>();

        StatDomains.Text = "—";
        StatOptimized.Text = "—";
        StatLatency.Text = "—";
        StatLatency.Foreground = Brush("#E8ECF5");
        StatTtfb.Text = "—";
        StatTtfb.Foreground = Brush("#E8ECF5");
        SummaryText.Text = "";
        BtnApply.IsEnabled = false;
        StartProgress(1);
    }

    private void UpdateStats(bool withProbe)
    {
        StatDomains.Text = _rows.Count.ToString();

        var applicable = _lastResults.Where(r => r.Applicable).ToList();
        StatOptimized.Text = applicable.Count.ToString();

        var pairs = _lastResults.Where(r => r.OldLatencyMs.HasValue && r.NewLatencyMs.HasValue).ToList();
        if (pairs.Count > 0)
        {
            var oldAvg = pairs.Average(r => r.OldLatencyMs!.Value);
            var newAvg = pairs.Average(r => r.NewLatencyMs!.Value);
            StatLatency.Text = $"{oldAvg:F0} → {newAvg:F0} ms";
            StatLatency.Foreground = newAvg < oldAvg ? Brush("#4ADE80") : Brush("#E8ECF5");
        }

        if (withProbe && _beforeProbes.Count > 0)
        {
            var ids = _beforeProbes.Keys.Where(k => _afterProbes.ContainsKey(k)).ToList();
            var valid = ids.Where(k => _beforeProbes[k].Ok && _afterProbes[k].Ok).ToList();

            if (valid.Count > 0)
            {
                var before = valid.Average(k => _beforeProbes[k].TimeToFirstByteMs);
                var after = valid.Average(k => _afterProbes[k].TimeToFirstByteMs);
                StatTtfb.Text = $"{before:F0} → {after:F0} ms";
                StatTtfb.Foreground = after < before ? Brush("#4ADE80") : Brush("#E8ECF5");
            }
            else
            {
                var recovered = ids.Count(k => !_beforeProbes[k].Ok && _afterProbes[k].Ok);
                StatTtfb.Text = recovered > 0 ? $"恢复 {recovered} 项" : "—";
                StatTtfb.Foreground = recovered > 0 ? Brush("#4ADE80") : Brush("#E8ECF5");
            }
        }

        SummaryText.Text = BuildShortSummary(applicable.Count);
    }

    private string BuildShortSummary(int applicableCount)
    {
        if (_lastResults.Count == 0) return "";

        var sb = new StringBuilder();
        sb.Append($"检测 {_lastResults.Count} 个域名");

        if (applicableCount > 0)
            sb.Append($" · 优化 {applicableCount} 个");
        else
            sb.Append(" · 全部已是最优");

        var failed = _lastResults.Count(r => !r.OldLatencyMs.HasValue && !r.NewLatencyMs.HasValue);
        if (failed > 0) sb.Append($" · {failed} 个不可用");

        return sb.ToString();
    }

    private string BuildSummary(int appliedCount)
    {
        var sb = new StringBuilder();
        sb.Append($"完成：写入 {appliedCount} 条优化记录。");

        var pairs = _lastResults.Where(r => r.OldLatencyMs.HasValue && r.NewLatencyMs.HasValue).ToList();
        if (pairs.Count > 0)
        {
            var oldAvg = pairs.Average(r => r.OldLatencyMs!.Value);
            var newAvg = pairs.Average(r => r.NewLatencyMs!.Value);
            sb.Append($" 平均延迟 {oldAvg:F0} → {newAvg:F0} ms。");
        }

        var probeIds = _beforeProbes.Keys.Where(k => _afterProbes.ContainsKey(k)).ToList();
        var valid = probeIds.Where(k => _beforeProbes[k].Ok && _afterProbes[k].Ok).ToList();
        if (valid.Count > 0)
        {
            var before = valid.Average(k => _beforeProbes[k].TimeToFirstByteMs);
            var after = valid.Average(k => _afterProbes[k].TimeToFirstByteMs);
            sb.Append($" 平均首字节 {before:F0} → {after:F0} ms。");
        }

        var fallback = _lastResults.Count(r => r.UsedFallback && r.Selected);
        if (fallback > 0)
            sb.Append($" 其中 {fallback} 条来自应急兜底 IP，不代表网络已恢复。");

        return sb.ToString();
    }

    private void RecordPhase(string name, Stopwatch stopwatch) =>
        _phaseSeconds[name] = stopwatch.Elapsed.TotalSeconds;

    private void ShowTimings()
    {
        if (_phaseSeconds.Count == 0)
        {
            TimingText.Text = "";
            return;
        }

        // 并发阶段会重叠，总耗时按整条流水线的墙钟时间算，避免把重叠部分重复相加
        var total = _pipelineSeconds > 0 ? _pipelineSeconds : _phaseSeconds.Values.Sum();
        var detail = string.Join(" · ", _phaseSeconds.Select(kv => $"{kv.Key} {kv.Value:F1}s"));
        TimingText.Text = $"耗时：{detail} · 共 {total:F1}s";
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BtnOptimize.IsEnabled = !busy;
        BtnDetect.IsEnabled = !busy;
        BtnRestore.IsEnabled = !busy;
        BtnForceRefresh.IsEnabled = !busy;
        BtnTestDns.IsEnabled = !busy;
        BtnRestoreDns.IsEnabled = !busy;
        PresetList.IsEnabled = !busy;
        BtnApplyManualDns.IsEnabled = !busy;
        BtnFillCurrentDns.IsEnabled = !busy;
        BtnExportDns.IsEnabled = !busy;
        TxtPrimaryDns.IsEnabled = !busy;
        TxtSecondaryDns.IsEnabled = !busy;
        if (busy) BtnApply.IsEnabled = false;
        if (busy) BtnApplyDns.IsEnabled = false;
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private void StartProgress(int total)
    {
        _totalUnits = Math.Max(1, total);
        _doneUnits = 0;
        SetProgress(0, _totalUnits);
    }

    private void AdvanceProgress()
    {
        _doneUnits++;
        SetProgress(_doneUnits, _totalUnits);
    }

    private void SetProgress(double done, double total) =>
        Progress.Value = total <= 0 ? 0 : Math.Min(100, done / total * 100);
}
