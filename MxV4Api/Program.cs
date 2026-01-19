using MxV4Api.Services;

var builder = WebApplication.CreateBuilder(args);

// 1. 注册单例服务
builder.Services.AddSingleton<MxService>();
builder.Services.AddHostedService<MxService>(provider => provider.GetRequiredService<MxService>());

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// 配置 Kestrel 限制 (可选，防止过多并发连接)
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxConcurrentConnections = 100;
    options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(5); // 缩短保活时间
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// ============================================================
// 【关键修改 1】: 拦截 favicon.ico
// 浏览器会自动请求这个图标，如果进入 PLC 队列会造成干扰。
// 这里直接返回 204 No Content，不走后续逻辑。
// ============================================================
app.MapGet("/favicon.ico", () => Results.NoContent());

app.UseAuthorization();
app.MapControllers();

app.Run();