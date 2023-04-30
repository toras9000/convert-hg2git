#nullable enable
#r "nuget: Lestaly, 0.37.0"
using System.Threading;
using Lestaly;

// ローカル環境にイメージをビルドするスクリプト。
// スクリプトを用意するほどではないが、プライベートスクリプトと同じ場所に置くことで利用場面に想定があることを暗に示すための意味が強い。

return await Paved.RunAsync(configuration: o => o.AnyPause(), action: async () =>
{
    using var canceller = new CancellationTokenSource();
    using var handler = ConsoleWig.CancelKeyHandlePeriod(canceller);

    var composeFile = ThisSource.RelativeFile("docker-compose.yml");
    await CmdProc.ExecAsync("docker", new[] { "compose", "--file", composeFile.FullName, "build", }, canceller.Token, stdOutWriter: Console.Out).AsSuccessCode();
});
