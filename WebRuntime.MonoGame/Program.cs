using System;
using System.Net.Http;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace WebRuntime.MonoGame
{
    internal class Program
    {
        private static System.Threading.Tasks.Task Main(string[] args)
        {
            var builder = WebAssemblyHostBuilder.CreateDefault(args);
            // Mount selector: `#mg-blazor-root` so we don't collide with the
            // Playground page's own `#app` div when this runtime is loaded
            // inline into the Playground. The standalone wwwroot/index.html
            // ships its own `#app` div for the boot splash and a separate
            // `#mg-blazor-root` for Blazor to mount into.
            builder.RootComponents.Add<App>("#mg-blazor-root");
            builder.Services.AddScoped(sp => new HttpClient
            {
                BaseAddress = new Uri(builder.HostEnvironment.BaseAddress),
            });
            return builder.Build().RunAsync();
        }
    }
}
