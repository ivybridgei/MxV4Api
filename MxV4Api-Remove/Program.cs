using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.Win32;
// 注意：不再需要引用 TaskScheduler 库

namespace MxV4Api_Remove
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.Title = "MxV4Api 卸载/清理工具";
            Console.ForegroundColor = ConsoleColor.White;

            Console.WriteLine("==================================================");
            Console.WriteLine("   MxV4Api 服务终止与清理工具 (零依赖版)");
            Console.WriteLine("==================================================");
            Console.WriteLine();

            // 1. 强力终止进程
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("[1/4] 正在终止运行中的进程...");
            KillProcess("MxV4Api");
            Console.WriteLine("等待进程完全退出...");
            Thread.Sleep(2000);

            // 2. 清理注册表启动项
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("\n[2/4] 清理注册表启动项...");
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key != null && key.GetValue("MxV4Api") != null)
                    {
                        key.DeleteValue("MxV4Api");
                        Console.WriteLine("  [OK] HKCU\\...\\Run\\MxV4Api 已移除。");
                    }
                    else
                    {
                        Console.WriteLine("  [SKIP] 未发现注册表启动项。");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("  [ERROR] 注册表清理失败: " + ex.Message);
                Console.ForegroundColor = ConsoleColor.Cyan;
            }

            // 3. 清理启动文件夹快捷方式
            Console.WriteLine("\n[3/4] 清理启动文件夹...");
            try
            {
                string linkPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "MxV4Api.lnk");
                if (File.Exists(linkPath))
                {
                    File.Delete(linkPath);
                    Console.WriteLine("  [OK] 快捷方式已删除: " + linkPath);
                }
                else
                {
                    Console.WriteLine("  [SKIP] 未发现启动快捷方式。");
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("  [ERROR] 文件删除失败: " + ex.Message);
                Console.ForegroundColor = ConsoleColor.Cyan;
            }

            // 4. 清理旧版任务计划 (改为调用原生 CMD 命令)
            Console.WriteLine("\n[4/4] 清理遗留的任务计划...");
            try
            {
                // 直接调用系统命令 schtasks /Delete
                // /TN "任务名" /F (强制删除)
                ProcessStartInfo psi = new ProcessStartInfo("schtasks", "/Delete /TN \"MxV4Api_AutoRun\" /F");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;

                using (var p = Process.Start(psi))
                {
                    p.WaitForExit();
                    if (p.ExitCode == 0)
                    {
                        Console.WriteLine("  [OK] 任务 'MxV4Api_AutoRun' 已移除。");
                    }
                    else
                    {
                        // 假如任务本来就不存在，也会报错，我们可以认为是 SKIP
                        string err = p.StandardError.ReadToEnd();
                        if (err.Contains("系统找不到") || err.Contains("does not exist"))
                        {
                            Console.WriteLine("  [SKIP] 未发现遗留任务。");
                        }
                        else
                        {
                            Console.WriteLine("  [INFO] 任务清理结果: " + err.Trim());
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [INFO] 任务计划命令执行异常: " + ex.Message);
            }

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("==================================================");
            Console.WriteLine("   清理完成！您现在可以安全地覆盖或删除程序文件了。");
            Console.WriteLine("==================================================");
            Console.ResetColor();

            Console.WriteLine("\n按任意键退出...");
            Console.ReadKey();
        }

        // 辅助方法：杀进程
        static void KillProcess(string processName)
        {
            try
            {
                var processes = Process.GetProcessesByName(processName);
                if (processes.Length == 0)
                {
                    Console.WriteLine("  [SKIP] 未发现进程: " + processName);
                    return;
                }

                foreach (var p in processes)
                {
                    try
                    {
                        Console.Write("  正在终止 PID " + p.Id + " ... ");
                        p.Kill();
                        p.WaitForExit(1000);
                        Console.WriteLine("成功");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("失败 (" + ex.Message + ")");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [ERROR] 获取进程列表失败: " + ex.Message);
            }
        }
    }
}