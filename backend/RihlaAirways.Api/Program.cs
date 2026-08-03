using Microsoft.EntityFrameworkCore;
using RihlaAirways.Api.Data;
var builder = WebApplication.CreateBuilder(args);

// hook up our AppDbContext to the sql server container -
// reads the connection string from appsettings.Development.json,
// this is what lets every controller just ask for AppDbContext in its constructor
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddControllers();
builder.Services.AddOpenApi();
// swashbuckle instead of the new .net 10 openapi-only setup -
// gives us the classic /swagger page the whole team knows from the bootcamp
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();


var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();
app.Run();