# MxV4Api: Mitsubishi PLC Communication RESTful API Service

[中文说明](README_zh.md)

`MxV4Api` is a high-performance, high-reliability Mitsubishi PLC communication middleware designed for Industry 4.0 scenarios. Developed on **.NET 8**, it wraps the traditional **Mitsubishi MX Component (ActUtlType)** COM controls into a modern, easy-to-use **RESTful API**.

### 🌟 Core Pain Points Solved
When developing with Mitsubishi MX Component directly, developers often face challenges that this project addresses with mature, built-in solutions:

1.  **COM Single-Threaded Apartment (STA) Limitations**: 
    This project uses a dedicated **STA Thread Pool** for isolation, ensuring that commands for each PLC station are executed in a thread-safe manner without interfering with one another.
2.  **Deadlocks and Zombie Processes**: 
    A **Guardian + Worker** dual-process architecture is implemented. If the COM component encounters an unrecoverable deadlock or persistent failures, the Worker process triggers a **self-termination (suicide) mechanism**, allowing the Guardian to immediately relaunch it for true 24/7 unattended operation.
3.  **Bit-Address Access Efficiency**: 
    Features a built-in **Bit Alignment Algorithm**. It supports bulk reading and automatic unpacking of bit devices (M, X, Y, etc.), resolving offset issues typically encountered during cross-word reads.
4.  **Initialization Concurrency Conflicts**: 
    By utilizing a **Global Setup Lock** and **Staggered Pre-Warming** technology, the service prevents driver crashes caused by multiple PLC stations initializing simultaneously.

---

## 🚀 Key Features

-   **Multi-Station Parallelism**: Supports managing dozens of PLC Logical Station Numbers simultaneously.
-   **RESTful Interface**: Read/Write Word devices (D, W, ZR) and Bit devices (M, X, Y) via standard HTTP GET/POST requests.
-   **String Support**: Automatically handles ASCII encoding/decoding within PLC memory for direct reading of barcodes, product names, etc.
-   **High-Stability Heartbeat**: Built-in background heartbeat monitoring with an automatic "Deep Reconnect" routine (releasing COM objects, performing GC, and re-initializing) upon link failure.
-   **Logging & Monitoring**: Integrated with Serilog for daily rolling logs. Includes a `/api/logs` endpoint for real-time remote status monitoring.
-   **One-Click Deployment**: Supports CLI arguments for installing the service as an auto-start application via Registry and Startup folder redundancy.

---

## 🛠 Tech Stack

-   **Runtime**: .NET 8.0 (Windows x86)
-   **PLC Driver**: Mitsubishi MX Component (ActUtlTypeLib)
-   **Logging**: Serilog (Console + Daily Rolling File)
-   **Documentation**: Swagger / OpenAPI
-   **Architecture**: Dual-Process Watchdog + STA Thread Message Queueing

---

## 📖 API Examples

### 1. Read Data
**GET** `/api/plc/{stationId}/read/{device}/{length?}`
-   Supports both Word (e.g., D100) and Bit (e.g., M100) devices.
-   **Response**: `{ "data": [0, 1, 0, ...], "station": 1 }`

### 2. Read String
**GET** `/api/plc/{stationId}/read-string/{device}/{length}`
-   Automatically decodes ASCII strings stored in PLC registers.

### 3. Write Data
**POST** `/api/plc/{stationId}/write`
-   **Body**: `{ "device": "D100", "value": 123 }`

---

## 📦 Deployment Guide

1.  **Environment Prerequisites**: The server must have **Mitsubishi MX Component v4 or v5** installed. Configure your Logical Station Numbers using the *Communication Setup Utility* beforehand.
2.  **Compilation**:
    *   The project **must** be compiled for the `x86` architecture (as MX Component is a 32-bit COM control).
    *   Configure the stations to be initialized at startup in the `PreWarmStations` section of `appsettings.json`.
3.  **Execution**:
    *   Run `MxV4Api.exe`: Starts in Guardian mode.
    *   `MxV4Api.exe --install`: Configures auto-start on boot and runs in the background.
    *   `MxV4Api.exe --uninstall`: Removes all auto-start configurations.

---

## 🛡 Robustness Design

-   **STA Isolation**: Every PLC station has an independent task queue (`BlockingCollection`), preventing high-concurrency Web API requests from overwhelming the COM driver.
-   **Self-Healing Logic**:
    ```csharp
    if (_continuousFailures >= 5) {
        // Determine the COM environment is unrecoverable; 
        // Force exit to let the Guardian restart the process.
        Environment.Exit(1);
    }
    ```
-   **Bit Unpacking**: To satisfy the MX Component requirement that bit devices be read in multiples of 16 bits, the code automatically calculates offsets and performs bitwise shifting to return precise data.

---

## ⚠️ Warning & Disclaimer

**PLEASE READ CAREFULLY:**
This project is intended for educational and technical reference purposes only. Since PLC control involves industrial production safety, the author shall not be held liable for any hardware damage, production accidents, or personal injury resulting from the use of this software. **Extensive offline testing is mandatory before deploying this service in a production environment.**

---