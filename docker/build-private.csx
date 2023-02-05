#nullable enable
#r "nuget: ProcessX, 1.5.5"
#r "nuget: Lestaly, 0.29.0"
using Zx;
using Lestaly;

// buildx でイメージをビルドして宅内のプライベートレジストリに push するスクリプト。
// 自分で使うためだけのもの。

return await Paved.RunAsync(configuration: o => o.AnyPause(), action: async () =>
{
    var privateRegistry = "registry.toras.home";
    var privateBuilder = "private-builder";

    var composeFile = ThisSource.GetRelativeFile("docker-compose.yml");
    await $"docker login \"{privateRegistry}\"";
    await $"docker buildx use \"{privateBuilder}\"";
    await $"docker buildx bake --file \"{composeFile.FullName}\" --push";
});
