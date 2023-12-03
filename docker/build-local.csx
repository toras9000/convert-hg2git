#nullable enable
#r "nuget: Lestaly, 0.51.0"
using System.Threading;
using Lestaly;
using Lestaly.Cx;

// ローカル環境にイメージをビルドするスクリプト。
// スクリプトを用意するほどではないが、プライベートスクリプトと同じ場所に置くことで利用場面に想定があることを暗に示すための意味が強い。

return await Paved.RunAsync(config: o => o.AnyPause(), action: async () =>
{
    using var signal = ConsoleWig.CreateCancelKeyHandlePeriod();

    var composeFile = ThisSource.RelativeFile("docker-compose.yml");
    await "docker".args("compose", "--file", composeFile.FullName, "build").cancelby(signal.Token).result().success();
});
