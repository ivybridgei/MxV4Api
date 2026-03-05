using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Drawing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using Microsoft.Win32.TaskScheduler;
using MxV4Api.Services;
using Serilog;

using Task = System.Threading.Tasks.Task;

namespace MxV4Api
{
    internal class Program
    {
        // 【关键】WinForms 托盘控件强制要求主线程必须是 STA 模式
        [STAThread]
        public static void Main(string[] args)
        {
            // ========================================================================
            // 0. 初始化 Serilog 日志配置
            // ========================================================================
            string logPath = Path.Combine(AppContext.BaseDirectory, "logs", "log-.txt");

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
                .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
                .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
                .WriteTo.File(logPath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 15,
                    fileSizeLimitBytes: 50 * 1024 * 1024,
                    rollOnFileSizeLimit: true,
                    shared: true,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff}[{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();

            try
            {
                string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                string mode = args.Length > 0 ? args[0].ToLower() : "guardian";

                // 兼容旧版命令，防止旧脚本调用报错
                if (mode == "--install")
                {
                    UpgradeAndCleanLegacyAutoStart();
                    ToggleAutoStart(true, exePath);
                    return;
                }
                if (mode == "--uninstall")
                {
                    ToggleAutoStart(false, exePath);
                    UpgradeAndCleanLegacyAutoStart();
                    return;
                }

                // 模式分流
                if (mode == "--worker")
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
                Log.Fatal(ex, ">>> [Main] 程序意外终止");
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }

        // ========================================================================
        // 1. 托盘守护进程模式 (Guardian)
        // ========================================================================
        static void RunGuardian(string exePath)
        {
            // 防止 Guardian 多开
            using var mutex = new Mutex(false, "Global\\MxV4Api_Guardian_Lock", out bool createdNew);
            if (!createdNew)
            {
                MessageBox.Show("MxV4Api 守护进程已经在运行中！\n请在右下角系统托盘中查找。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 【无感升级】启动时自动清理旧版的启动项（任务计划/快捷方式），并统一使用注册表
            UpgradeAndCleanLegacyAutoStart();

            // 1. 初始化右键菜单
            var trayMenu = new ContextMenuStrip();

            var autoStartMenuItem = new ToolStripMenuItem("开机自启");
            autoStartMenuItem.Checked = IsAutoStartEnabled(exePath);
            autoStartMenuItem.Click += (s, e) =>
            {
                bool targetState = !autoStartMenuItem.Checked;
                bool success = ToggleAutoStart(targetState, exePath);

                if (success)
                {
                    autoStartMenuItem.Checked = targetState;
                }
            };

            var exitMenuItem = new ToolStripMenuItem("完全退出");

            trayMenu.Items.Add(autoStartMenuItem);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(exitMenuItem);

            // 2. 初始化托盘图标
            using var notifyIcon = new NotifyIcon
            {
                Text = "MxV4Api 通讯服务",
                ContextMenuStrip = trayMenu,
                Visible = true
            };

            // 尝试提取自身的图标，提取失败则用系统默认应用图标
            try { notifyIcon.Icon = Icon.ExtractAssociatedIcon(exePath) ?? SystemIcons.Application; }
            catch { notifyIcon.Icon = SystemIcons.Application; }

            // 3. 异步拉起并监控 Worker 子进程
            var cts = new CancellationTokenSource();
            Process currentWorkerProcess = null;

            _ = Task.Run(() =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        Log.Information(">>> [Guardian] 正在拉起 Worker 子进程...");
                        var psi = new ProcessStartInfo(exePath, "--worker")
                        {
                            WorkingDirectory = AppContext.BaseDirectory,
                            UseShellExecute = false,
                            CreateNoWindow = true // 隐藏 Worker 的控制台黑框
                        };

                        currentWorkerProcess = Process.Start(psi);
                        if (currentWorkerProcess != null)
                        {
                            Log.Information($">>> [Guardian] Worker PID: {currentWorkerProcess.Id}");
                            currentWorkerProcess.WaitForExit();

                            if (cts.IsCancellationRequested) break;
                            Log.Warning($">>> [Guardian] Worker 异常停止 (ExitCode: {currentWorkerProcess.ExitCode})，2秒后准备重启...");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, ">>> [Guardian] 启动 Worker 异常");
                    }

                    if (!cts.IsCancellationRequested)
                        Thread.Sleep(2000);
                }
            });

            // 4. 退出逻辑
            exitMenuItem.Click += (s, e) =>
            {
                Log.Information(">>> [Guardian] 收到完全退出指令，正在终止服务...");
                cts.Cancel(); // 停止重启循环

                try
                {
                    // 杀死当前的 Worker 子进程
                    if (currentWorkerProcess != null && !currentWorkerProcess.HasExited)
                    {
                        currentWorkerProcess.Kill();
                    }
                }
                catch { }

                notifyIcon.Visible = false;
                Application.Exit(); // 退出消息循环
            };

            Log.Information(">>> [Guardian] 守护进程已启动，系统托盘图标已加载");

            // 启动 Windows 消息循环（挂起主线程，响应右键菜单等 UI 事件）
            Application.Run();
        }

        // ========================================================================
        // 2. API 工作进程模式 (Worker)
        // ========================================================================
        static void RunWorker(string[] args)
        {
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

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(2000);
                    Log.Information(">>> [Worker] 触发后台预热任务...");
                    var mxManager = app.Services.GetRequiredService<MxService>();
                    await mxManager.PreWarmAsync();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, ">>>[Worker] 预热异常");
                }
            });

            app.Run();
        }

        // ========================================================================
        // 3. 自启管理与旧版清理助手
        // ========================================================================
        static bool IsAutoStartEnabled(string exePath)
        {
            try
            {
                using var ts = new TaskService();
                var task = ts.GetTask("MxV4Api_AutoRun_V4");
                if (task != null) return true;

                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
                var value = key?.GetValue("MxV4Api") as string;
                return value != null && value.Contains(exePath, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        static bool ToggleAutoStart(bool enable, string exePath)
        {
            string taskName = "MxV4Api_AutoRun_V4";
            try
            {
                using var ts = new TaskService();
                if (enable)
                {
                    var td = ts.NewTask();
                    td.RegistrationInfo.Description = "MxV4Api 通讯服务开机自启";
                    td.Principal.RunLevel = TaskRunLevel.Highest;
                    td.Triggers.Add(new LogonTrigger());
                    td.Actions.Add(new ExecAction(exePath, null, AppContext.BaseDirectory));
                    td.Settings.DisallowStartIfOnBatteries = false;
                    td.Settings.StopIfGoingOnBatteries = false;
                    td.Settings.ExecutionTimeLimit = TimeSpan.Zero;

                    ts.RootFolder.RegisterTaskDefinition(taskName, td);
                    Log.Information("[Guardian] 已通过任务计划程序开启开机自启 (支持高权限/Win10)");

                    using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                    key?.SetValue("MxV4Api", $"\"{exePath}\"");
                }
                else
                {
                    ts.RootFolder.DeleteTask(taskName, false);
                    Log.Information("[Guardian] 已关闭任务计划开机自启");

                    using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                    key?.DeleteValue("MxV4Api", false);
                }

                return true;
            }
            catch (UnauthorizedAccessException)
            {
                Log.Error("[Guardian] 权限不足，无法修改任务计划程序。");
                MessageBox.Show("设置开机自启失败！\nWindows 10 要求必须具有管理员权限才能设置高权限自启。\n\n解决办法：请退出当前程序，然后【右键 -> 以管理员身份运行】再来勾选此项。",
                    "权限不足", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Guardian] 切换开机自启发生未知错误");
                MessageBox.Show($"设置开机自启发生错误: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        /// <summary>
        /// 无感升级：清理旧版（V1/V2/V3）残留的任务计划和快捷方式
        /// </summary>
        static void UpgradeAndCleanLegacyAutoStart()
        {
            // 1. 清理旧版的 Windows 任务计划程序
            try
            {
                using var ts = new TaskService();
                if (ts.GetTask("MxV4Api_AutoRun") != null)
                {
                    ts.RootFolder.DeleteTask("MxV4Api_AutoRun");
                    Log.Information("[Upgrade] 检测到并清理了旧版任务计划启动项。");
                }
            }
            catch { } // 权限不足或找不到则忽略

            // 2. 清理旧版的系统启动文件夹快捷方式
            try
            {
                string linkPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "MxV4Api.lnk");
                if (File.Exists(linkPath))
                {
                    File.Delete(linkPath);
                    Log.Information("[Upgrade] 检测到并清理了旧版启动文件夹快捷方式。");
                }
            }
            catch { }
        }
    }
}