#nullable enable
#r "nuget: Docker.DotNet, 3.125.15"
#r "nuget: Kurukuru, 1.4.2"
#r "nuget: Lestaly, 0.51.0"
using System.Formats.Tar;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using System.Threading;
using CommandLine;
using Docker.DotNet;
using Docker.DotNet.Models;
using Kurukuru;
using Lestaly;

// Mercurial から Git リポジトリへの変換。
// 準備済みの作者名マッピングファイルなども指定して変換を行う。増分変換などにも利用可。

// コマンドラインパラメータをマッピングする型
class Options
{
    [Option('h', "hgrepo", HelpText = "変換対象の mercurial リポジトリディレクトパス", Required = true)]
    public string? HgRepo { get; set; }

    [Option('g', "gitrepo", HelpText = "変換後 git リポジトリを作成するディレクトリパス", Required = true)]
    public string? GitRepo { get; set; }

    [Option('a', "author-map", HelpText = "利用する作者名マッピングファイルパス", Required = true)]
    public string? AuthorMap { get; set; }

    [Option('m', "win32mbcs", HelpText = "リポジトリで win32mbcs を使用していたか否か", Required = true)]
    public bool? UseMbcs { get; set; }

    [Option('d', "default-branch", HelpText = "default ブランチの変換後ブランチ名")]
    public string? DefaultBranchName { get; set; }

    [Option('e', "file-enc", HelpText = "リポジトリで win32mbcs を使用していた場合のファイル名エンコーディング")]
    public string? MbcsEncoding { get; set; }

    [Option('f', "force", HelpText = "警告を無視しての強制実行フラグ")]
    public bool? Force { get; set; }
}

// スクリプトの設定
var settings = new
{
    // デフォルト設定：win32mbcs が使われていた場合のファイル名エンコーディング
    DefaultMbcsEncoding = "cp932",

    // デフォルト設定：Mercurial リポジトリ default ブランチの変換後ブランチ名
    DefaultBranchName = "main",

    // 処理完了時に一時停止するか否か。
    NoPause = false,

    // 指定されたGitディレクトリが存在しないときにinitするか否か。
    AllowGitInit = true,

    // fast-export のイメージ名
    ImageName = "toras9000/fast-export:latest",

    // イメージが存在しない場合に自動ビルドするか否か
    ImageAutoBuild = true,

    // 自動ビルドする際のコンテキストディレクトリ
    ImageBuildContext = "./docker/build",

    // ビルド引数：fast-export 取得ブランチ
    ImageBuildArgBranch = "v221024",
};

/// <summary>Dockerコンテナコンテキストを表すレコード</summary>
/// <param name="Client">Dockerクライアントオブジェクト</param>
/// <param name="ContainerId">対象コンテナID</param>
/// <param name="CancelToken">実行中止トークン</param>
record ContainerContext(DockerClient Client, string ContainerId)
{
    /// <summary>コンテナ内で追加のプロセスを実行して出力を取得する</summary>
    /// <param name="cmd">コマンドライン</param>
    /// <param name="workDir">ワーキングディレクトリ</param>
    /// <returns>結果を得るタスク</returns>
    public async Task<(long code, string stdout, string stderr)> ExecAsync(IList<string> cmd, string? workDir = null, CancellationToken cancelToken = default)
    {
        var execParam = new ContainerExecCreateParameters();
        execParam.Cmd = cmd;
        execParam.WorkingDir = workDir;
        execParam.AttachStdout = true;
        execParam.AttachStderr = true;
        var token = await this.Client.Exec.ExecCreateContainerAsync(this.ContainerId, execParam, cancelToken);
        using var stream = await this.Client.Exec.StartWithConfigContainerExecAsync(token.ID, new(), cancelToken);
        var output = await stream.ReadOutputToEndAsync(cancelToken);
        var inspect = await this.Client.Exec.InspectContainerExecAsync(token.ID, cancelToken);
        return (inspect.ExitCode, output.stdout, output.stderr);
    }
}

// メイン処理
return await Paved.RunAsync(config: o => GC.KeepAlive(settings.NoPause ? o.NoPause() : o.AnyPause()), action: async () =>
{
    // 出力エンコーディングを設定
    using var outenc = ConsoleWig.OutputEncodingPeriod(Encoding.UTF8);

    // コマンドライン引数をパース
    var options = CliArgs.Parse<Options>(Args);

    // 変換元ディレクトリ。
    var hgDir = CurrentDir.RelativeDirectoryAt(options.HgRepo) ?? throw new PavedMessageException("変換元ディレクトリが指定されていません。");
    if (hgDir.RelativeDirectoryAt(".hg")?.Exists != true) throw new PavedMessageException("指定された変換元が mercurial リポジトリではありません。");

    // 変換先ディレクトリ。
    // 存在しない場合はinit許可設定ならばそのまま継続
    var gitDir = CurrentDir.RelativeDirectoryAt(options.GitRepo) ?? throw new PavedMessageException("変換先ディレクトリが指定されていません。");
    var gitDirInit = !gitDir.Exists && settings.AllowGitInit;
    if (!gitDirInit && gitDir.RelativeDirectoryAt(".git")?.Exists != true) throw new PavedMessageException("指定された変換先が git リポジトリではありません。");

    // 利用する作者名マッピングファイル
    var authorsFile = CurrentDir.RelativeFileAt(options.AuthorMap) ?? throw new PavedMessageException("変換先ディレクトリが指定されていません。");

    // タイムスタンプ文字列
    var timestamp = $"{DateTime.Now:yyyyMMdd_HHmmss}";

    // win32mbcs 利用状態の明示的な設定を必須とする。
    var useMbcs = options.UseMbcs ?? throw new PavedMessageException("win32mbcs の利用有無を指定する必要があります。");

    // Mercurial リポジトリ default ブランチの変換後ブランチ名
    var defBranch = options.DefaultBranchName.OmitWhite() ?? settings.DefaultBranchName;

    // ファイル名エンコーディングの決定
    var fileEnc = options.MbcsEncoding.OmitWhite() ?? settings.DefaultMbcsEncoding;

    // 強制実行フラグが明示的に指定されていなければOFF
    var useForce = options.Force ?? false;

    // キャンセルキーハンドラ
    using var canceller = new CancellationTokenSource();
    using var handler = ConsoleWig.CancelKeyHandlePeriod(canceller);

    // 作業用一時ディレクトリ作成
    using var tmpDir = new TempDir();

    // コンテナ内のパス
    var containerFs = new
    {
        MercurialRepo = "/work/src",
        GitRepo = "/work/dest",
        DataDir = "/work/tmp",
        FastExport = "/work/fast-export/hg-fast-export.sh",
    };

    // Docker クライアントオブジェクト正生成
    using var client = await Spinner.StartAsync("Docker アクセス準備 ...", async spinner =>
    {
        await Task.CompletedTask;
        return new DockerClientConfiguration().CreateClient();
    });

    // イメージが存在するかチェック
    var image = await Try.FuncOrDefaultAsync(() => client.Images.InspectImageAsync(settings.ImageName, canceller.Token));
    if (image == null)
    {
        // 自動ビルド無効にしていなければイメージをビルド
        if (!settings.ImageAutoBuild) throw new PavedMessageException($"イメージが存在しません。");
        await Spinner.StartAsync("イメージをビルド ...", async spinner =>
        {
            // 実行時間の目安のために経過秒数を表示
            var caption = spinner.Text;
            var watch = Stopwatch.StartNew();
            using var timer = new Timer(_ => spinner.Text = $"{caption} {watch.ElapsedMilliseconds / 1000} seconds", null, 0, 400);
            // ビルド資材を tar にアーカイブ
            var buildFile = ThisSource.RelativeDirectory(settings.ImageBuildContext).RelativeFile("Dockerfile");
            using var contentsTar = new MemoryStream();
            using var writer = new TarWriter(contentsTar);
            writer.WriteEntry(buildFile.FullName, buildFile.Name);
            contentsTar.Seek(0, SeekOrigin.Begin);
            // ビルド実行
            var emptyObserver = new Progress<JSONMessage>();
            var buildParam = new ImageBuildParameters();
            buildParam.Dockerfile = buildFile.Name;
            buildParam.Tags = new[] { settings.ImageName };
            buildParam.BuildArgs = new Dictionary<string, string> { ["TARGET_BRANCH"] = settings.ImageBuildArgBranch, };
            await client.Images.BuildImageFromDockerfileAsync(buildParam, contentsTar, null, null, emptyObserver, canceller.Token);
        });
    }

    // 実行用 Docker コンテナ開始
    var container = await Spinner.StartAsync("fast-export コンテナの起動 ...", async spinner =>
    {
        var parameter = new CreateContainerParameters();
        parameter.Image = settings.ImageName;
        parameter.Tty = true;
        parameter.HostConfig = new HostConfig();
        parameter.HostConfig.Mounts = new List<Mount>();
        parameter.HostConfig.Mounts.Add(new() { Type = "bind", Source = hgDir.FullName, Target = containerFs.MercurialRepo, ReadOnly = true, });
        parameter.HostConfig.Mounts.Add(new() { Type = "bind", Source = gitDir.FullName, Target = containerFs.GitRepo, });
        parameter.HostConfig.Mounts.Add(new() { Type = "bind", Source = tmpDir.Info.FullName, Target = containerFs.DataDir, });
        return await client.Containers.CreateContainerAsync(parameter, canceller.Token);
    });

    // 変換処理
    try
    {
        // コンテナを開始
        var running = await client.Containers.StartContainerAsync(container.ID, new(), canceller.Token);
        if (!running) throw new PavedMessageException($"コンテナの実行を開始できません。");

        // 実行環境にするコンテナ情報生成
        var context = new ContainerContext(client, container.ID);

        // 変換処理で使う作者名マッピングファイル情報。この後の処理でこのパスに準備する。
        var authorMapFile = tmpDir.Info.RelativeFile($"authors.txt");

        // 利用する作者名マッピングファイルを参照場所にコピー
        authorsFile.CopyTo(authorMapFile.FullName);

        // git リポジトリが存在していなくて init する場合
        if (gitDirInit)
        {
            Console.WriteLine();
            await Spinner.StartAsync("Gitリポジトリ初期化 ...", async spinner =>
            {
                var gitInitResult = await context.ExecAsync(new[] { "git", "init", "--initial-branch", defBranch, }, workDir: containerFs.GitRepo, canceller.Token);
                if (gitInitResult.code != 0) throw new PavedMessageException($"Git リポジトリを初期化できません。");
                var gitConfigResult = await context.ExecAsync(new[] { "git", "config", "core.ignoreCase", "false", }, workDir: containerFs.GitRepo, canceller.Token);
                if (gitConfigResult.code != 0) throw new PavedMessageException($"Git リポジトリを構成できません。");
            });
        }

        // 変換スクリプトの呼び出し
        var convResult = await Spinner.StartAsync($"コミット変換 to {gitDir.Name} ...", async spinner =>
        {
            // 実行時間の目安のために経過秒数を表示
            var caption = spinner.Text;
            var watch = Stopwatch.StartNew();
            using var timer = new Timer(_ => spinner.Text = $"{caption} {watch.ElapsedMilliseconds / 1000} seconds", null, 0, 400);
            // 変換プロセス実行
            var cmdLine = new List<string>()
            {
                "bash", containerFs.FastExport,
                "-r", containerFs.MercurialRepo,
                "-A", $"{containerFs.DataDir}/{authorMapFile.Name}",
                "-M", defBranch,
            };
            if (useMbcs) cmdLine.AddRange(new[] { "--fe", fileEnc, });
            if (useForce) cmdLine.Add("--force");
            return await context.ExecAsync(cmdLine, workDir: containerFs.GitRepo, canceller.Token);
        });

        // ログ保存
        using (var logWriter = CurrentDir.RelativeFile($"log_{timestamp}.txt").CreateTextWriter())
        {
            if (convResult.stdout.IsNotEmpty())
            {
                await logWriter.WriteLineAsync("<<<stdout>>");
                await logWriter.WriteLineAsync(convResult.stdout);
            }
            if (convResult.stderr.IsNotEmpty())
            {
                await logWriter.WriteLineAsync("<<<stderr>>");
                await logWriter.WriteLineAsync(convResult.stderr);
            }
        }

        // 変換失敗していたらエラーにする
        if (convResult.code != 0) throw new PavedMessageException($"コミット変換に失敗しました。");

        // リポジトリの初期化を伴う変換をしたときは HEAD をチェックアウト
        if (gitDirInit)
        {
            var checkoutResult = await context.ExecAsync(new[] { "git", "checkout", "HEAD", }, workDir: containerFs.GitRepo, canceller.Token);
            if (checkoutResult.code != 0) throw new PavedMessageException($"変換後リポジトリのワーキングツリーをチェックアウト出来ませんでした。");
        }
    }
    finally
    {
        // コンテナを破棄
        await client.Containers.RemoveContainerAsync(container.ID, new() { Force = true, });
    }
});
