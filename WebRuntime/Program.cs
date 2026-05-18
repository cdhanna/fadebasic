using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using System.Runtime.InteropServices.JavaScript;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

var host = builder.Build();

// Load the JS module that backs WebInterop's [JSImport] methods.
// Must complete before any command that uses location()/user_agent()/alert is called.
// Path is relative to where the .NET runtime loaded from (/_framework/),
// so "../web-commands.js" points at wwwroot/web-commands.js.
await JSHost.ImportAsync("web-commands", "../web-commands.js");

await host.RunAsync();
