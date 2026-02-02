using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Win32;
using Microsoft.Win32.TaskScheduler;
using MxV4Api.Services;
using Serilog; // 【新增】引用 Serilog

// ========================================================================
// 0. 初始化 Serilog 日志配置 (精简版)
// ========================================================================
string logPath = Path.Combine(AppContext.BaseDirectory, "logs", "log-.txt");

Log.Logger = new LoggerConfiguration()
    // 1. 全局最低级别：Information (为了保留我们的业务日志)
    .MinimumLevel.Information()
    
    // 2. 【关键】强制屏蔽 Microsoft 和 System 的 Info 日志
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)

    // 3. 写入文件配置
    .WriteTo.Console()
    .WriteTo.File(logPath, 
        rollingInterval: RollingInterval.Day, 
        retainedFileCountLimit: 15,
        fileSizeLimitBytes: 50 * 1024 * 1024,
        rollOnFileSizeLimit: true,
        shared: true, // 【关键】开启共享写入，支持多进程 (Guardian + Worker)
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    // ========================================================================
    // 1. 启动模式分流 (Guardian / Worker / Install)
    // ========================================================================
    string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
    string workDir = AppContext.BaseDirectory;

    string mode = "guardian"; // 默认模式

    if (args.Length > 0)
    {
        string arg0 = args[0].ToLower();
        if (arg0 == "--worker") mode = "worker";
        else if (arg0 == "--install") mode = "install";
        else if (arg0 == "--uninstall") mode = "uninstall";
    }

    if (mode == "install")
    {
        InstallAutoStart(exePath, workDir);
        return;
    }
    if (mode == "uninstall")
    {
        UninstallAutoStart();
        return;
    }

    if (mode == "worker")
    {
        RunWorker(args);
    }
    else
    {
        RunGuardian(exePath);
    }
}
catch (Exception ex)
{
    // 捕获启动过程中的致命错误
    Log.Fatal(ex, ">>> [Main] 程序意外终止");
}
finally
{
    Log.CloseAndFlush();
}

// ========================================================================
// 业务逻辑方法
// ========================================================================

static void RunWorker(string[] args)
{
    // 防止 Worker 多开互斥锁
    using var mutex = new Mutex(false, "Global\\MxV4Api_Worker_Lock", out bool createdNew);
    if (!createdNew)
    {
        Log.Warning("Worker 实例已存在，当前进程退出。");
        return;
    }

    Log.Information(">>> [Worker] API 服务正在启动...");

    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();

    builder.Services.AddSingleton<MxService>();
    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();

    builder.WebHost.ConfigureKestrel(options =>
    {
        options.Limits.MaxConcurrentConnections = null;
        options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
    });

    var app = builder.Build();

    app.UseSwagger();
    app.UseSwaggerUI();

    app.MapGet("/favicon.ico", () => Results.NoContent());
    app.UseAuthorization();
    app.MapControllers();

    // 预热任务
    _ = System.Threading.Tasks.Task.Run(async () =>
    {
        try
        {
            // 稍作延迟，等待 Server Ready
            await System.Threading.Tasks.Task.Delay(2000);
            Log.Information(">>> [Worker] 触发后台预热任务...");
            var mxManager = app.Services.GetRequiredService<MxService>();
            await mxManager.PreWarmAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, ">>> [Worker] 预热异常");
        }
    });

    app.Run();
}

static void RunGuardian(string exePath)
{
    // 防止 Guardian 多开
    using var mutex = new Mutex(false, "Global\\MxV4Api_Guardian_Lock", out bool createdNew);
    if (!createdNew)
    {
        return; // 静默退出
    }

    Log.Information(">>> [Guardian] 守护进程已启动 (可双击托盘图标或查看日志)");

    while (true)
    {
        try
        {
            Log.Information(">>> [Guardian] 正在拉起 Worker 子进程...");

            var psi = new ProcessStartInfo(exePath, "--worker")
            {
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = false
            };

            var p = Process.Start(psi);
            if (p != null)
            {
                Log.Information($">>> [Guardian] Worker PID: {p.Id}");
                p.WaitForExit();
                Log.Warning($">>> [Guardian] Worker 停止 (ExitCode: {p.ExitCode})，准备重启...");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, ">>> [Guardian] 启动 Worker 异常");
        }

        // 避免 CPU 此时狂转
        Thread.Sleep(2000);
    }
}

static void InstallAutoStart(string exePath, string workDir)
{
    Console.WriteLine("=== 安装开机自启 (Registry + Startup) ===");
    
    // 1. 清理旧版
    UninstallAutoStart(cleanOnly: true);

    try
    {
        // 2. 注册表启动
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
        {
            if (key != null)
            {
                key.SetValue("MxV4Api", $"\"{exePath}\"");
                Console.WriteLine("[成功] 注册表启动项已添加。");
            }
        }

        // 3. 启动文件夹快捷方式 (PowerShell)
        string linkPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "MxV4Api.lnk");
        string pScript = $@"
$w=New-Object -com WScript.Shell;
$s=$w.CreateShortcut('{linkPath}');
$s.TargetPath='{exePath}';
$s.WorkingDirectory='{workDir}';
$s.Save()";
        
        var psi = new ProcessStartInfo("powershell", $"-Command \"{pScript}\"") { CreateNoWindow=true, UseShellExecute=false };
        Process.Start(psi)?.WaitForExit();
        Console.WriteLine("[成功] 启动文件夹快捷方式已创建。");
        
        // 4. 尝试立即启动
        Console.WriteLine("正在启动守护进程...");
        Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Error] {ex.Message}");
    }
}

static void UninstallAutoStart(bool cleanOnly = false)
{
    if (!cleanOnly) Console.WriteLine("=== 卸载开机自启 ===");

    // 1. 清理任务计划 (旧版)
    try
    {
        using (var ts = new TaskService())
        {
            if (ts.GetTask("MxV4Api_AutoRun") != null)
            {
                ts.RootFolder.DeleteTask("MxV4Api_AutoRun");
                if (!cleanOnly) Console.WriteLine("[成功] 旧版任务计划程序已清理。");
            }
        }
    }
    catch { /* 忽略 */ }

    // 2. 清理注册表
    try
    {
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
        {
            if (key != null && key.GetValue("MxV4Api") != null)
            {
                key.DeleteValue("MxV4Api");
                if (!cleanOnly) Console.WriteLine("[成功] 注册表启动项已移除。");
            }
        }
    }
    catch { }

    // 3. 清理启动文件夹
    try
    {
        string linkPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "MxV4Api.lnk");
        if (File.Exists(linkPath))
        {
            File.Delete(linkPath);
            if (!cleanOnly) Console.WriteLine("[成功] 启动便签已移除。");
        }
    }
    catch { }
}