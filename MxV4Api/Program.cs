using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
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
    //    这能减少 90% 的 HTTP 请求日志
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)

    // 3. 写入文件配置
    .WriteTo.Console()
    .WriteTo.File(logPath, 
        rollingInterval: RollingInterval.Day, 
        retainedFileCountLimit: 15, // 缩减保留天数到15天 (既然日志量大，存太久也没用)
        fileSizeLimitBytes: 50 * 1024 * 1024, // 单个文件限制 50MB
        rollOnFileSizeLimit: true, // 超过 50MB 就切新文件，防止单个文件过大打不开
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}") // 简化格式，去掉 SourceContext
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
            InstallTask(args);
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
        options.Limits.MaxConcurrentConnections = null;
        options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
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
// 3. 任务计划程序安装逻辑 (标准开机自启版 - Session 1)
// ========================================================================
static void InstallTask(string[] args)
{
    const string TaskName = "MxV4Api_AutoRun";
    string exePath = Process.GetCurrentProcess().MainModule?.FileName;
    string workDir = AppContext.BaseDirectory;

    if (string.IsNullOrEmpty(exePath)) return;

    // 1. 获取用户名逻辑 (保持不变)
    string user = "Administrator";
    if (args.Length > 1 && !string.IsNullOrWhiteSpace(args[1]))
    {
        user = args[1];
        Console.WriteLine($"使用命令行指定的账户: {user}");
    }
    else
    {
        try
        {
            string currentUser = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
            Console.Write($"请输入运行账户 (默认 {currentUser}): ");
            string input = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(input)) user = input.Trim();
            else user = currentUser;
        }
        catch (IOException)
        {
            Console.WriteLine("\n[Win7兼容模式] 使用默认账户: Administrator");
            user = "Administrator";
        }
    }

    Console.WriteLine($"\n配置目标：用户 [{user}] 登录时自动启动...");

    try
    {
        using (TaskService ts = new TaskService())
        {
            // 清理旧任务
            var existingTask = ts.GetTask(TaskName);
            if (existingTask != null) ts.RootFolder.DeleteTask(TaskName);

            TaskDefinition td = ts.NewTask();
            td.RegistrationInfo.Description = "PLC 接口服务 (登录自启)";
            td.RegistrationInfo.Author = user;

            // =========================================================
            // 触发器：仅在登录时触发一次
            // =========================================================
            var logonTrigger = new LogonTrigger();
            logonTrigger.UserId = user;
            logonTrigger.Delay = TimeSpan.FromSeconds(10); // 登录后延迟10秒，等待桌面加载
            logonTrigger.Enabled = true;
            td.Triggers.Add(logonTrigger);

            // =========================================================
            // 设置：标准后台运行配置
            // =========================================================
            // 如果程序已在运行，忽略新的启动请求（防止重复）
            td.Settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;

            // 允许无限制运行
            td.Settings.ExecutionTimeLimit = TimeSpan.Zero;
            td.Settings.Priority = ProcessPriorityClass.High;
            td.Settings.DisallowStartIfOnBatteries = false;
            td.Settings.StopIfGoingOnBatteries = false;

            // 【可选】崩溃自动重启 (仅针对程序崩溃，不针对手动Kill)
            // 如果不需要可以注释掉下面两行
            td.Settings.RestartCount = 3;
            td.Settings.RestartInterval = TimeSpan.FromMinutes(1);

            // =========================================================
            // 操作与权限
            // =========================================================
            td.Actions.Add(new ExecAction(exePath, null, workDir));

            // 关键：交互式令牌 (Session 1)
            td.Principal.RunLevel = TaskRunLevel.Highest;
            td.Principal.LogonType = TaskLogonType.InteractiveToken;

            // 注册任务
            ts.RootFolder.RegisterTaskDefinition(
                TaskName,
                td,
                TaskCreation.CreateOrUpdate,
                user,
                null,
                TaskLogonType.InteractiveToken);

            Console.WriteLine("\n[成功] 服务已安装！");
            Console.WriteLine("机制：配置了 Windows 自动登录后，开机即启动。");

            try
            {
                var task = ts.GetTask(TaskName);
                if (task != null)
                {
                    task.Run();
                    Console.WriteLine("[成功] 任务已立即启动。");
                }
            }
            catch (Exception runEx)
            {
                Console.WriteLine($"[注意] 立即启动受限 ({runEx.Message})，请重启电脑测试。");
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n[失败] {ex.Message}");
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