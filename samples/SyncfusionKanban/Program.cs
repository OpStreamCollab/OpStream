using SyncfusionKanban.Components;
using Syncfusion.Blazor;

namespace SyncfusionKanban
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();

            // Optional: register your Syncfusion license key here to remove the trial watermark.
            // Syncfusion.Licensing.SyncfusionLicenseProvider.RegisterLicense("YOUR-KEY");
            builder.Services.AddSyncfusionBlazor();

            builder.Services.AddOpStreamClient()
                .UseSignalRTransport(options =>
                {
                    options.HubUrl = builder.Configuration["OpStream:HubUrl"]
                        ?? "http://localhost:50109/collab";
                });

            var app = builder.Build();

            if (!app.Environment.IsDevelopment())
                app.UseExceptionHandler("/Error");

            app.UseAntiforgery();
            app.MapStaticAssets();
            app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode();

            app.Run();
        }
    }
}
