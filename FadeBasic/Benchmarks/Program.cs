// See https://aka.ms/new-console-template for more information

using BenchmarkDotNet.Running;
using Benchmarks;
using MoonSharp.Interpreter;

// Script.RunString("a = 3 + 2");

var summary = BenchmarkRunner.Run<Json>();