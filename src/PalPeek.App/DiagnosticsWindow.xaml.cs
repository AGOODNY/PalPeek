using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace PalPeek;

public partial class DiagnosticsWindow : System.Windows.Controls.UserControl, INotifyPropertyChanged
{
    private readonly DiagnosticsService _diagnostics;
    private bool _isRefreshing;
    private bool _hasRefreshed;
    private string _summaryText = "正在等待检查…";

    public DiagnosticsWindow(DiagnosticsService diagnostics)
    {
        _diagnostics = diagnostics;
        InitializeComponent();
        DataContext = this;
    }

    public ObservableCollection<DiagnosticItem> Items { get; } = new();

    public string SummaryText
    {
        get => _summaryText;
        private set
        {
            if (_summaryText == value)
                return;
            _summaryText = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_isRefreshing)
            return;

        _isRefreshing = true;
        RefreshButton.IsEnabled = false;
        RefreshButton.Content = "正在检查…";
        SummaryText = "正在读取当前链路状态…";
        try
        {
            var report = await _diagnostics.CaptureAsync();
            _hasRefreshed = true;
            Items.Clear();
            foreach (var item in report.Items)
                Items.Add(item);

            var success = report.Items.Count(item => item.Level == DiagnosticLevel.Success);
            var failure = report.Items.Count(item => item.Level == DiagnosticLevel.Failure);
            var notice = report.Items.Count(item => item.Level == DiagnosticLevel.Notice);
            var waiting = report.Items.Count - success - failure - notice;
            SummaryText =
                $"{report.GeneratedAt:HH:mm:ss} · 正常 {success} · 异常 {failure} · 提示 {notice} · 等待 {waiting}";
        }
        catch (Exception ex)
        {
            Items.Clear();
            Items.Add(new DiagnosticItem(
                "诊断服务",
                DiagnosticLevel.Failure,
                $"无法完成诊断：{ex.Message}",
                "稍后重试；如果问题持续存在，请重新启动 PalPeek。"));
            SummaryText = "诊断未完成。";
        }
        finally
        {
            RefreshButton.Content = "刷新检查";
            RefreshButton.IsEnabled = true;
            _isRefreshing = false;
        }
    }

    public async Task RefreshIfNeededAsync()
    {
        if (!_hasRefreshed)
            await RefreshAsync();
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
