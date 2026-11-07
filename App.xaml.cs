using System.Windows;
using NetBoost.Core;

namespace NetBoost;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 隐藏自检参数：跑完 DoH/TCP/TLS/netsh 后写日志退出，不打开界面
        if (SelfTest.IsRequested(e.Args))
        {
            var code = Task.Run(SelfTest.RunAsync).GetAwaiter().GetResult();
            Environment.Exit(code);
            return;
        }

        // 提权子进程只执行动作，不打开主界面
        if (Elevation.TryRunFromCommandLine(e.Args))
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);

        var window = new MainWindow();
        this.MainWindow = window;
        window.Show();
    }
}
