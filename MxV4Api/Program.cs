using MxV4Api.Services;

var builder = WebApplication.CreateBuilder(args);

// 1. 注册 MxService 为单例托管服务 (HostedService 会自动调用 StartAsync)
builder.Services.AddSingleton<MxService>();
builder.Services.AddHostedService<MxService>(provider => provider.GetRequiredService<MxService>());

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// 配置 HTTP 请求管道
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthorization();
app.MapControllers();

app.Run();