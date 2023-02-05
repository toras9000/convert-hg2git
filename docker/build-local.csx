#nullable enable
#r "nuget: ProcessX, 1.5.5"
#r "nuget: Lestaly, 0.29.0"
using Zx;
using Lestaly;

// ローカル環境にイメージをビルドするスクリプト。
// スクリプトを用意するほどではないが、プライベートスクリプトと同じ場所に置くことで利用場面に想定があることを暗に示すための意味が強い。

return await Paved.RunAsync(configuration: o => o.AnyPause(), action: async () =>
{
    var composeFile = ThisSource.GetRelativeFile("docker-compose.yml");
    await $"docker compose --file \"{composeFile.FullName}\" build";
});
