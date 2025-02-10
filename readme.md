<img src="https://github.com/cdhanna/fadebasic/blob/main/images/ghost_lee.png?raw=true" width="100" alt="The ghost of Lee">

# Fade Basic 

_Fade's actually Dotnet embeddable_ 

```basic
FOR n = 1 to 10
    PRINT n
NEXT n
```

----

_Fade Basic_ is a dialect of BASIC inspired by my personal love for [Dark Basic Pro](https://www.reddit.com/r/DarkBasicDev/). My father downloaded _Dark Basic_ sometime circa 2003 and showed a then-kid version of me how to program a FOR-LOOP. That moment sparked a life long obsession with programming and game development. I wanted to create something that kindled the same joy for programming that _Dark Basic_ did all those years ago. I created _Fade Basic_ for myself, to fuel my inner nostalgia for a by-gon era of my own life. And I figured I'd share it, because I think it is cool. 

_Fade_ is a scripting language for dotnet. _Fade_ code runs inside a dotnet process. It is debuggable. It uses zero Reflection. The _Fade_ code gets compiled into a custom byte-code, and then that byte-code is interpreted by a custom state machine. The language has [almost](https://github.com/cdhanna/fadebasic/blob/main/FadeBasic/FadeBasic/FadeBasic.csproj#L22) no dependencies, which means it can run wherever dotnet can run. For now, I have been focusing on stand-alone applications that bootstrap a _Fade_ script as their primary execution, but it is possible to embed _Fade_ scripts into any dotnet compatible program, such as Godot, Unity, an ASP.NET Server, or a custom program. 

This project is under active development and is only available in a preview-capacity. 


## Getting Started

#### Prerequisites 

Before you can get started, 
1. [dotnet 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) (or later) installed on your machine. Verify the installation by opening a terminal and checking the `dotnet` version, 
    ```sh
    dotnet --version
    ```

2. Download [Visual Studio Code](https://code.visualstudio.com/download), because it is the only IDE I have added developer tooling for.

#### Install _Fade Basic_

1. Download the templates package from [nuget](https://www.nuget.org/packages/FadeBasic.Templates.Common) using the following command, 
    ```sh
    dotnet new install FadeBasic.Templates.Common
    ```
2. In _Visual Studio Code_, install the [Fade Basic Extension](https://marketplace.visualstudio.com/items?itemName=BrewedInk.fadebasic). 

#### Your first _Fade_ program

1. Create a new folder and use `dotnet` to create a new project from the templates you just installed. 
    ```sh
    mkdir tunaFade
    cd tunaFade
    dotnet new fadebasic-project-console -n tunaFade
    ```

2. Open _Visual Studio Code_ to the folder containing your project, 
    ```sh
    code .
    ```

    You should see a few files, 
    | filename | description |
    | -------- | ----------- |
    | `main.fbasic` | This is where your program source code is! |
    | `tunaFade.csproj` | By default, this template creates a standalone dotnet console application, and this `.csproj` file is the build configuration file for the console application. This file contains the version of _Fade_ you are using. |

3. Open the `main.fbasic` file, and you should see the following contents... 
    ```basic
    remstart 
        tunaProject main entry point
    remend

    print "hello world"
    ```

> [!CAUTION]
> Unfortunately there is a [known issue](https://github.com/cdhanna/fadebasic/issues/1) for first time setup where the syntax highlighting doesn't work until you reboot _Visual Studio Code_. 

4. Click on the run and debug tab in _Visual Studio Code_, and select the big blue _Run and Debug_ button. You shouldn't need to specify any new launch configuration, because one should appear automatically. The `.vscode/launch.json` file should look like the following, 
    ```jsonc
    {
        // Use IntelliSense to learn about possible attributes.
        // Hover to view descriptions of existing attributes.
        // For more information, visit: https://go.microsoft.com/fwlink/?linkid=830387
        "version": "0.2.0",
        "configurations": [
        
            {
                "type": "fadeBasicDebugger",
                "request": "launch",
                "name": "Fade Debug",
                "program": "${workspaceFolder}/${command:AskForProgramName}"
            }
        ]
    }
    ```

5. Now, click the green play button to run the `Fade Debug` launch configuration. You should see `"hello world"` appear in the _Terminal_ tab of _Visual Studio Code_. 

    Congratulations! You just ran your first _Fade_ program! 

6. _Bonus:_ If you hover your mouse over the `print` command, you should see a popup that provides a small amount of documentation for the command. Click the link that says, `Full Documentation`, and a web browser should open to a `localhost` address showing a list of all available commands. 


#### Next Steps
Now that you have _Fade_ running locally, you should check out the full language specification! Or learn how to debug your program? Maybe even create a custom command collection. 

- [Language Specification](https://github.com/cdhanna/fadebasic/blob/main/FadeBasic/book/FadeBook/Language.md)
- Debugger (docs yet to be written)
- Custom Commands (docs yet to be written)
- Language Implementation Notes (docs yet to be written)
- Differences between _Dark Basic_ and _Fade_ (docs yet to be written)

## Contact and Help

_Fade Basic_ is un active development. As such, I am sure there are many bugs I don't know about yet. If you find one of them, please tell me about it! 
Feel free to join the [Discord](https://discord.gg/d7Q5EuQc), or post a Github Issue. 

## License  

_Fade Basic_ has many parts, and at the moment, all of them are MIT licensed. I plan on keeping the core language and tooling systems open sourced under the MIT license. However, at some point in the future, I reserve the right to release closed-source extensions or domain specific command collections. 

## Contributing 

I welcome feedback, but at the moment, I am not expecting to accept contributions to the project. I have a vision for _Fade_, and I want to see that vision to completion. Thank you for your consideration. 
