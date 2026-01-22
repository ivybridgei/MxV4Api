using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Win32.TaskScheduler;
using MxV4Api.Services;
using Serilog; // 【新增】引用 Serilog

// ========================================================================
// 0. 初始化 Serilog 日志配置
// ========================================================================
// 设置日志路径为当前 EXE 目录下的 logs 文件夹
string logPath = Path.Combine(AppContext.BaseDirectory, "logs", "log-.txt");

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information() // 最小日志级别
                                // 写入控制台 (方便调试时看)
    .WriteTo.Console()
    // 【关键】写入文件，按天滚动，保留 30 天
    .WriteTo.File(logPath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30)
    .CreateLogger();

try
{
    // ========================================================================
    // 1. 命令行参数拦截 (安装/卸载逻辑)
    // ========================================================================
    if (args.Length > 0)
    {
        string command = args[0].ToLower();
        if (command == "--install")
        {
            InstallTask();
            return;
        }
        if (command == "--uninstall")
        {
            UninstallTask();
            return;
        }
    }

    Log.Information(">>> 程序正在启动...");

    // ========================================================================
    // 2. WebAPI 主程序逻辑
    // ========================================================================
    var builder = WebApplication.CreateBuilder(args);

    // 【新增】将 Serilog 替换为系统的默认日志提供程序
    builder.Host.UseSerilog();

    builder.Services.AddSingleton<MxService>();
    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();

    builder.WebHost.ConfigureKestrel(options =>
    {
        options.Limits.MaxConcurrentConnections = 200;
        options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(5);
    });

    var app = builder.Build();

    // 即使在生产环境也开启 Swagger，方便您查看
    app.UseSwagger();
    app.UseSwaggerUI();

    app.MapGet("/favicon.ico", () => Results.NoContent());
    app.UseAuthorization();
    app.MapControllers();

    // ============================================================
    // 执行异步预热
    // ============================================================
    _ = System.Threading.Tasks.Task.Run(async () =>
    {
        try
        {
            Log.Information(">>> 触发后台预热任务...");
            var mxManager = app.Services.GetRequiredService<MxService>();
            await mxManager.PreWarmAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, ">>> 预热异常");
        }
    });

    app.Run();
}
catch (Exception ex)
{
    // 捕获启动过程中的致命错误
    Log.Fatal(ex, ">>> 程序意外终止");
}
finally
{
    Log.CloseAndFlush();
}


// ========================================================================
// 3. 任务计划程序安装逻辑 (交互模式 - Session 1)
// ========================================================================
static void InstallTask()
{
    const string TaskName = "MxV4Api_AutoRun";
    string exePath = Process.GetCurrentProcess().MainModule?.FileName;
    string workDir = AppContext.BaseDirectory;

    if (string.IsNullOrEmpty(exePath))
    {
        Console.WriteLine("[错误] 无法获取执行文件路径。");
        return;
    }

    Console.WriteLine("==================================================");
    Console.WriteLine("正在安装 PLC 接口服务 (交互模式 - Session 1)");
    Console.WriteLine("此模式将配置为：[用户登录时自动运行]");
    Console.WriteLine("这能完美解决 MX Component 在后台服务中连接失败的问题。");
    Console.WriteLine("请确保您已配置了 Windows 自动登录 (AutoLogon)。");
    Console.WriteLine("==================================================");

    // 获取当前用户，或者让用户输入
    string currentUser = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
    Console.Write($"请输入运行账户 (默认 {currentUser}): ");
    string user = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(user)) user = currentUser;

    // 【修改点1】：不需要输入密码了，因为是“仅当用户登录时运行”
    Console.WriteLine($"\n将在用户 [{user}] 登录时自动启动...");

    try
    {
        using (TaskService ts = new TaskService())
        {
            // 如果已存在则删除旧的
            var existingTask = ts.GetTask(TaskName);
            if (existingTask != null) ts.RootFolder.DeleteTask(TaskName);

            TaskDefinition td = ts.NewTask();
            td.RegistrationInfo.Description = "基于 .NET 8 和 MX Component v4 的 PLC 读写接口服务 (Session 1 桌面模式)";
            td.RegistrationInfo.Author = user;

            // --- 【修改点2】触发器：登录时启动 (LogonTrigger) ---
            var logonTrigger = new LogonTrigger();
            logonTrigger.UserId = user; // 指定监听哪个用户的登录
            logonTrigger.Delay = TimeSpan.FromSeconds(10); // 登录后延迟10秒，等待桌面加载完毕
            logonTrigger.Enabled = true;
            td.Triggers.Add(logonTrigger);

            // --- 操作：启动程序 ---
            td.Actions.Add(new ExecAction(exePath, null, workDir));

            // --- 核心设置：失败重启 ---
            td.Settings.RestartCount = 999;
            td.Settings.RestartInterval = TimeSpan.FromMinutes(1);
            td.Settings.ExecutionTimeLimit = TimeSpan.Zero;
            td.Settings.Priority = ProcessPriorityClass.High;
            td.Settings.DisallowStartIfOnBatteries = false;
            td.Settings.StopIfGoingOnBatteries = false;
            // 允许多实例运行设为 False (IgnoreNew)，防止重复启动
            td.Settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;

            // --- 【修改点3】权限设置：交互式令牌 ---
            // RunLevel.Highest: 以管理员权限运行
            // LogonType.InteractiveToken: 仅当用户登录时运行 (核心关键！)
            td.Principal.RunLevel = TaskRunLevel.Highest;
            td.Principal.LogonType = TaskLogonType.InteractiveToken;

            // --- 注册任务 (不需要密码) ---
            ts.RootFolder.RegisterTaskDefinition(
                TaskName,
                td,
                TaskCreation.CreateOrUpdate,
                user,
                null, // 密码为 null
                TaskLogonType.InteractiveToken);

            Console.WriteLine("\n[成功] 服务已安装！");
            Console.WriteLine("[重要] 请配合 Sysinternals Autologon 工具实现服务器开机自动登录。");

            try
            {
                Console.WriteLine("[提示] 正在尝试立即启动任务...");
                var task = ts.GetTask(TaskName);
                if (task != null)
                {
                    task.Run();
                    Console.WriteLine("[成功] 任务已启动。请通过 http://IP:5000 验证。");
                }
            }
            catch (Exception runEx)
            {
                Console.WriteLine($"[注意] 立即启动受限 ({runEx.Message})，但这通常没问题。");
                Console.WriteLine("请注销并重新登录，或重启电脑以测试自动启动。");
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n[失败] 安装出错: {ex.Message}");
    }
}


static void UninstallTask()
{
    const string TaskName = "MxV4Api_AutoRun";
    try
    {
        using (TaskService ts = new TaskService())
        {
            var task = ts.GetTask(TaskName);
            if (task != null)
            {
                ts.RootFolder.DeleteTask(TaskName);
                Console.WriteLine($"[成功] 服务 '{TaskName}' 已卸载。");
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[失败] 卸载出错: {ex.Message}");
    }
}