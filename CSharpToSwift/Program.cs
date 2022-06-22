using CSharpToSwift;

// See https://aka.ms/new-console-template for more information
Console.WriteLine("Hello, World!");

string projectFilePath = "/Users/fak/Dropbox/Projects/Circuit/CircuitMauiLib/CircuitMauiLib.csproj";


var transpiler = new Transpiler(projectFilePath);
await transpiler.TranspileAsync();
