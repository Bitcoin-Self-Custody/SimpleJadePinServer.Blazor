using SimpleJadePinServer.Blazor.Services;

var builder = WebApplication.CreateBuilder(args);

// Wire up Blazor Web App with interactive server rendering
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// KeyStorageService initialises on startup — generates or loads the server EC keypair
// and derives the AES pin-data key used by PinStorageService.
// We construct it here (not via DI factory) so we can call Initialize() before registration.
var keyDataPath = Path.Combine(builder.Environment.ContentRootPath, "key_data");
var keyStorageService = new KeyStorageService(keyDataPath);
keyStorageService.Initialize();

builder.Services.AddSingleton(keyStorageService);

// PinStorageService needs the AES key that KeyStorageService just derived
builder.Services.AddSingleton(new PinStorageService(keyDataPath, keyStorageService.AesPinData.ToArray()));

// PinCryptoService depends on both — satisfield by the two singletons above
builder.Services.AddSingleton<PinCryptoService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseAntiforgery();

// Map all Razor components; the entry-point component is App (in Components/App.razor)
app.MapRazorComponents<SimpleJadePinServer.Blazor.Components.App>()
    .AddInteractiveServerRenderMode();

app.Run();
