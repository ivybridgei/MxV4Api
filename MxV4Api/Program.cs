using MxV4Api.Services;

var builder = WebApplication.CreateBuilder(args);

// 注册 MxService (单例)
builder.Services.AddSingleton<MxService>();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Kestrel 配置
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxConcurrentConnections = 200;
    options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(5);
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapGet("/favicon.ico", () => Results.NoContent());
app.UseAuthorization();
app.MapControllers();

// ============================================================
// 【新增】执行预热逻辑
// 必须在 app.Run() 之前调用
// ============================================================
try
{
    var mxManager = app.Services.GetRequiredService<MxService>();
    // 这将读取 appsettings.json 并立即连接 PLC
    mxManager.PreWarm();
}
catch (Exception ex)
{
    Console.WriteLine($"预热失败: {ex.Message}");
}

app.Run();