# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.0.62] - 2026-04-19
### Fixed
- xml docs that would fail to parse during doc generation will log warnings instead of failing the build
- ref commands work for `float` args

## [0.0.61] - 2026-04-16
### Added
- Fade.Build generates markdown file for command docs unless `<FadeDisableAutoDocs>` is set to true

### Fixed
- generated docs allow slashes

## [0.0.60] - 2026-04-03
### Added
- debug session supports dynamic restarts without losing connection to DAP

### Fixed
- launcher under debug no longer exits on first debug message

## [0.0.59] - 2026-04-01
### Fixed
- debugger shows hover values when looking at struct fields and array fields
- debugger statement eval shows output

## [0.0.58] - 2026-03-24
### Fixed
- debugger handles suspended vm correctly
- debugger sorts through tokens using binary search and dense `int` array

## [0.0.57] - 2026-03-24
### Fixed
- debugger suspends execution when a debug message is received
- debugger can re-pause at breakpoint 

## [0.0.56] - 2026-03-24
### Changed
- `DebugSession` is mostly virtual, and can be overriden

## [0.0.55] - 2026-03-24
### Fixed
- debugger no longer uses infinite budget

## [0.0.54] - 2026-03-24

### Fixed
- completion handler shows correct casing for variables
- completion handler supports #constant values

## [0.0.53] - 2026-03-23

### Added
- debugger has repl

### Fixed
- debugger hover statements work for strings
- debugger issue when stepping over `return` statement

## [0.0.52] - 2026-03-23

### Fixed
- debugger hits breakpoints with instruction indexes mid-token 
- debugger can modify last field
- debugger evals properly resize register buffers

## [0.0.51] - 2026-03-21

### Added
- LSP supports text completions
- LSP supports basic rename support
- LSP supports signature helper

### Fixed
- debugger program performance greatly improved
- type tokens appear highlighted in declaration statements

## [0.0.50] - 2026-03-06

### Fixed
- `not` appears highlighted as keyword instead of comment
- command help appears again in LSP
- load `.dll` files into memory before streaming, to avoid file locking

## [0.0.49] - 2026-02-24

### Fixed
- Build tasks copies files into shadow directory

## [0.0.48] - 2026-02-24

### Fixed
- able to use variable in `REPEAT` conditionals that was defined within the `REPEAT` block

## [0.0.47] - 2026-02-22

### Added
- `defer` functionality 
- `#macro` functionality

### Fixed
- `gosub` and `goto` work inside functions
- memory ref-counts strings returned from commands 

## [0.0.46] - 2025-11-25

### Fixed
- expressions can be multiline

## [0.0.45] - 2025-06-19

### Fixed
- `FadeSdk` errors use source maps to include file location in error messages
- Parser reports error when an object initializer includes non assignment statements
- Parser reports error when invalid-cast occurs in the variable assignment of for-loops
- Usage of global variables before they are declared reports a different error than simply an undefined symbol

### Added
- Simple array assignment 
- `redim` keyword allows arrays to be resized

## [0.0.44] - 2025-06-05

### Fixed
- Debugger can open arrays of structs

## [0.0.43] - 2025-06-04

### Fixed
- String concatenation no longer causes virtual memory leak 
- Debugger can receive large payloads

## [0.0.42] - 2025-05-29

### Added
- `FadeRuntimeContext` is now an `ILaunchable` implementation
- new `Fade.GetFadeFilesFromProject()` function allows user to find fade files in a csproj file

## [0.0.41] - 2025-05-20

### Fixed
- Object initializers can be used inside all program blocks 

## [0.0.40] - 2025-05-08

### Fixed
- Constant values appear as macros in Language Server Protocol (LSP)

## [0.0.39] - 2025-04-27

### Fixed
- Object initializers can be used inside function blocks

## [0.0.38] - 2025-03-07

### Added
- The `default` keyword
- Object initializer pattern

## [0.0.37] - 2025-03-02

### Changed
- VM register max count increased from `byte` to `ulong`.
- VM heap max size increased from ~ 2^31 to ~ 2^64. 

## [0.0.36] - 2025-02-26

### Fixed
- Debugger would emit exit event during partial budget runs. 
- Debugger can render `byte` variables.
- Debugger can set non `int` and non `float` values.
- Debugger can discovery variables declared through `ref` commands, such as 
  `INC` or `INPUT`
- Commands with `ref` parameters can access globally scoped variables.

### Added
- `SKIP` keyword for skipping iterations of looping control structures. 