# MxV4Api: 三菱 PLC 通讯 RESTful 接口服务

`MxV4Api` 是一个专为工业 4.0 场景设计的、高性能且高可靠的三菱 PLC 通讯中台。它基于 .NET 8 开发，将传统的三菱 **MX Component (ActUtlType)** 控件包装为现代化的 **RESTful API**。

### 🌟 核心痛点解决
在直接使用三菱 MX Component 进行二次开发时，开发者通常会遇到以下难题，本项目均已提供成熟方案：
1.  **COM 组件单线程限制**：本项目通过专属的 **STA (Single-Threaded Apartment) 线程池** 隔离，确保每个 PLC 站点的指令执行安全且互不干扰。
2.  **死锁与僵尸进程**：引入了 **Guardian（守护进程）+ Worker（工作进程）** 的双进程架构。当检测到 COM 组件发生不可恢复的死锁或连续失败时，Worker 会触发“自杀”机制，由守护进程立即拉起，实现真正的无人值守。
3.  **位地址读取效率**：内置了位地址对齐算法（Bit Alignment），支持对 M/X/Y 等位元件的批量读取及自动解包，解决跨字读取导致的偏移问题。
4.  **初始化并发冲突**：通过 **全局驱动锁 (Global Setup Lock)** 和 **错峰预热 (Staggered Pre-Warm)** 技术，防止多个 PLC 站点同时初始化导致驱动崩溃。

---

## 🚀 主要功能

-   **多站并行**：支持同时管理数十个 PLC 逻辑站号（Logical Station Number）。
-   **RESTful 接口**：通过标准的 HTTP GET/POST 请求即可完成 D/W/ZR 字元件和 M/X/Y 位元件的读写。
-   **字符串支持**：自动处理 PLC 内存中的 ASCII 编码转换，直接读取 PLC 内的条码、名称等字符串。
-   **高稳定性心跳**：内置后台心跳检查，实时监控通讯链路，异常时自动执行深度重连（释放 COM 对象、垃圾回收、重新初始化）。
-   **日志与自研监控**：集成 Serilog 滚动日志，并提供 `/api/logs` 接口，支持远程实时查看运行状态。
-   **一键部署**：支持命令行参数安装开机自启，支持注册表及启动文件夹双重备份。

---

## 🛠 技术栈

-   **Runtime**: .NET 8.0 (Windows x86)
-   **PLC Driver**: Mitsubishi MX Component (ActUtlTypeLib)
-   **Logging**: Serilog (Console + Daily File)
-   **Documentation**: Swagger / OpenAPI
-   **Architecture**: Double-Process Watchdog + STA Thread Messaging Queue

---

## 📖 接口示例

### 1. 读取数据 (Read)
**GET** `/api/plc/{stationId}/read/{device}/{length?}`
-   支持读取字元件 (D100) 或 位元件 (M100)。
-   **返回**：`{ "data": [0, 1, 0, ...], "station": 1 }`

### 2. 读取字符串 (Read String)
**GET** `/api/plc/{stationId}/read-string/{device}/{length}`
-   自动解析 PLC 寄存器中的 ASCII 码。

### 3. 写入数据 (Write)
**POST** `/api/plc/{stationId}/write`
-   **Body**: `{ "device": "D100", "value": 123 }`

---

## 📦 部署指南

1.  **环境准备**：服务器必须安装三菱官方 **MX Component v4 或 v5**，并使用通讯配置工具（Communication Setup Utility）配置好逻辑站号。
2.  **编译配置**：
    *   项目必须以 `x86` 架构编译（因为 MX Component 是 32 位 COM 控件）。
    *   在 `appsettings.json` 中配置需要预热启动的 `PreWarmStations`。
3.  **运行**：
    *   直接运行 `MxV4Api.exe`：进入守护进程模式。
    *   `MxV4Api.exe --install`：安装开机自启并后台运行。
    *   `MxV4Api.exe --uninstall`：卸载相关服务。

---

## 🛡 鲁棒性设计说明

-   **STA 隔离**：每个 PLC 站点拥有独立的任务队列（BlockingCollection），防止 Web API 的高并发请求直接冲击 COM 驱动。
-   **自愈逻辑**：
    ```csharp
    if (_continuousFailures >= 5) {
        // 判定 COM 组件处于不可恢复状态，强制退出由守护进程重启
        Environment.Exit(1);
    }
    ```
-   **位解包算法**：针对三菱位元件必须以 16 倍数地址读取的限制，代码自动计算 Offset 并进行位移运算。

---

## ⚠️ 警告与免责声明：
本项目仅供学习和技术参考。由于 PLC 控制涉及工业生产安全，因使用本软件导致的任何硬件损坏、生产事故或人身伤害，作者不承担任何法律责任。在正式环境使用前，请务必进行充分的离线测试。

---