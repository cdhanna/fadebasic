# Fade Basic

_Fade's actually Dotnet embeddable_ 

**This project is still in active development, and the documentation is not complete**

_Fade Basic_ is a dialect of BASIC inspired by my personal love for [Dark Basic Pro](https://www.reddit.com/r/DarkBasicDev/). I am still working on the documentation. 

Very basically, _Fade_ code gets compiled into a custom bytecode that is interpreted inside a running dotnet process. Compiled _Fade_ code can run anywhere where dotnet can run. I've made sure to keep the dependencies of the core _Fade_ project as small as possible to maximize portability. 

To get started, you must have the [dotnet 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) (or later) installed on your machine. Also, you should download Visual Studio Code, because that is the only IDE I have added developer tooling for.

Then, within VSCode, download the [Fade Basic Extension](https://marketplace.visualstudio.com/items?itemName=BrewedInk.fadebasic). 

Use `dotnet` to install some project templates, 

```sh
dotnet new install FadeBasic.Templates.Common
```

And finally, create a sample console project. 
```sh
dotnet new fadebasic-project-console 
```

## Contact and Help

Feel free to join the [Discord](https://discord.gg/d7Q5EuQc), or post a Github Issue. 