#nullable enable
#r "nuget: Lestaly, 0.51.0"
using System.Threading;
using Lestaly;
using Lestaly.Cx;

// buildx でイメージをビルドして宅内のプライベートレジストリに push するスクリプト。
// 自分で使うためだけのもの。

return await Paved.RunAsync(config: o => o.AnyPause(), action: async () =>
{
    var privateRegistry = "registry.toras.home";
    var privateBuilder = "private-builder";

    using var signal = ConsoleWig.CreateCancelKeyHandlePeriod();

    var composeFile = ThisSource.RelativeFile("docker-compose.yml");
    await "docker".args("login", privateRegistry).cancelby(signal.Token).result().success();
    await "docker".args("buildx", "use", privateBuilder).cancelby(signal.Token).result().success();
    await "docker".args("buildx", "bake", "--file", composeFile.FullName, "--push").cancelby(signal.Token).result().success();
});
