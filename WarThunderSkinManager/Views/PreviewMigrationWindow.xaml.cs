using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using WarThunderSkinManager.Models;
using WarThunderSkinManager.Services;

namespace WarThunderSkinManager.Views;

/// <summary>
/// 预览图迁入资源库的**一次性**进度窗（升级到新布局后的首次启动）。
/// </summary>
/// <remarks>
/// **没有取消 / 关闭**：迁移可以在任意时刻被强杀或断电，下次启动会从断点继续（逐包幂等）；
/// 但界面本身不提供"跳过"入口 —— 预览图属于涂装数据，必须落到库里。
/// </remarks>
public partial class PreviewMigrationWindow : Window
{
    private readonly AppConfig _config;

    private bool _finished;

    /// <summary>迁移结论（窗口关闭后由启动流程读取）。</summary>
    public PreviewMigrationResult? Result { get; private set; }

    public PreviewMigrationWindow(AppConfig config)
    {
        _config = config;
        InitializeComponent();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            var progress = new Progress<PreviewMigrationProgress>(p =>
            {
                if (p.Total <= 0)
                {
                    Bar.IsIndeterminate = true;
                    return;
                }

                Bar.IsIndeterminate = false;
                Bar.Value = (double)p.Done / p.Total;
                DetailText.Text = LocalizationManager.Instance.Format(
                    "migrate.preview.progress", p.Done, p.Total, p.Current);
            });

            Result = await Task.Run(() => PreviewMigrationService.Run(_config, progress));
        }
        catch (Exception ex)
        {
            Result = new PreviewMigrationResult(0, 0, 0, 0, false, ex.GetBaseException().Message);
        }
        finally
        {
            _finished = true;
            Close();
        }
    }

    /// <summary>迁移没结束时不允许关闭（Alt+F4 也挡掉）；完成后由 <see cref="RunAsync"/> 自己关。</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_finished)
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }
}
