# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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