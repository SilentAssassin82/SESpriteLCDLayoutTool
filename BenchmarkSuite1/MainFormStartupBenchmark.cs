using BenchmarkDotNet.Attributes;
using Microsoft.VSDiagnostics;
using SESpriteLCDLayoutTool;

namespace SESpriteLCDLayoutTool.Benchmarks
{
    [CPUUsageDiagnoser]
    public class MainFormStartupBenchmark
    {
        [Benchmark]
        public void CreateAndDisposeMainForm()
        {
            using (var form = new MainForm())
            {
            }
        }
    }
}