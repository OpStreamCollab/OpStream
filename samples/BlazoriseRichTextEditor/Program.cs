using Blazorise;
using BlazoriseRichTextEditor.Components;

using Blazorise.Icons.FontAwesome;
using Blazorise.Bootstrap;
using Blazorise.RichTextEdit;

namespace BlazoriseRichTextEditor
{
    public class Program
    {
        public static void Main( string[] args )
        {
            var builder = WebApplication.CreateBuilder( args );

            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();

            builder.Services
                .AddBlazorise( options =>
                {
                    options.Immediate = true;
                } )
                .AddBootstrapProviders()
                .AddFontAwesomeIcons();

            builder.Services.AddBlazoriseRichTextEdit();

            builder.Services.AddOpStreamClient()
                .UseSignalRTransport( options =>
                {
                    options.HubUrl = builder.Configuration["OpStream:HubUrl"]
                        ?? "http://localhost:50109/collab";
                } );

            var app = builder.Build();

            if ( !app.Environment.IsDevelopment() )
            {
                app.UseExceptionHandler( "/Error" );
            }

            app.UseAntiforgery();

            app.MapStaticAssets();
            app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode();

            app.Run();
        }
    }
}
