using CommandLine;

namespace MaxDon.ApimUpdater;

internal class Program
{
    static async Task<int> Main(string[] args)
    {
        return await Parser.Default.ParseArguments<UpdateApiCommand>(args)
            .MapResult(t => t.ExecuteAsync(), err => Task.FromResult(1));
    }
}