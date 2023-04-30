#nullable enable
#r "nuget: Lestaly, 0.37.0"
using Lestaly;
using System.Threading;

// buildx でイメージをビルドして宅内のプライベートレジストリに push するスクリプト。
// 自分で使うためだけのもの。

return await Paved.RunAsync(configuration: o => o.AnyPause(), action: async () =>
{
    var privateRegistry = "registry.toras.home";
    var privateBuilder = "private-builder";

    using var canceller = new CancellationTokenSource();
    using var handler = ConsoleWig.CancelKeyHandlePeriod(canceller);

    var composeFile = ThisSource.RelativeFile("docker-compose.yml");
    await CmdProc.ExecAsync("docker", new[] { "login", privateRegistry, }, canceller.Token, stdOutWriter: Console.Out).AsSuccessCode();
    await CmdProc.ExecAsync("docker", new[] { "buildx", "use", privateBuilder, }, canceller.Token, stdOutWriter: Console.Out).AsSuccessCode();
    await CmdProc.ExecAsync("docker", new[] { "buildx", "bake", "--file", composeFile.FullName, "--push", }, canceller.Token, stdOutWriter: Console.Out).AsSuccessCode();
});
