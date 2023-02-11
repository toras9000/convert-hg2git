#nullable enable
#r "nuget: Docker.DotNet, 3.125.12"
#r "nuget: Kurukuru, 1.4.2"
#r "nuget: Lestaly, 0.29.0"
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

// Docker イメージを使って Mercurial リポジトリを git リポジトリに変換する。
// なお、主に Docker.DotNet の利用方法把握を目的として作成したので、おそらく docker-compose をコマンド呼び出しするほうが楽に作れそう。

// コマンドラインパラメータをマッピングする型
class Options
{
    [Value(0, HelpText = "変換対象の mercurial リポジトリディレクトパス")]
    public string? Target { get; set; }

    [Option('o', "outdir", HelpText = "変換後 git リポジトリを作成するディレクトリパス")]
    public string? OutDir { get; set; }

    [Option('a', "author-map", HelpText = "利用する作者名マッピングファイルパス")]
    public string? AuthorMap { get; set; }

    [Option('m', "win32mbcs", HelpText = "リポジトリで win32mbcs を使用していたか否か")]
    public bool? UseMbcs { get; set; }

    [Option('e', "file-enc", HelpText = "リポジトリで win32mbcs を使用していた場合のファイル名エンコーディング")]
    public string? MbcsEncoding { get; set; }

    [Option('f', "force", HelpText = "警告を無視しての強制実行フラグ")]
    public bool? Force { get; set; }

    [Option('r', "rename-manual", HelpText = "作者名マッピングの手動リネームモード")]
    public bool? ManualRename { get; set; }

    [Option('r', "copy-authors", HelpText = "作者名マッピングファイルをコピーして残すか否か (ファイル指定がない場合)")]
    public bool? CopyAuthorMap { get; set; }
}

// スクリプトの設定
var settings = new
{
    // fast-export のイメージ名
    ImageName = "my/fast-export:latest",

    // デフォルト設定：win32mbcs が使われていた場合のファイル名エンコーディング
    DefaultMbcsEncoding = "cp932",

    // デフォルト設定：作者名マッピングの手動リネームモード
    DefaultManualRename = false,

    // デフォルト設定：作者名マッピングファイルをコピーして残すか否か
    DefaultCopyAuthorMap = false,

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
return await Paved.RunAsync(configuration: o => o.AnyPause(), action: async () =>
{
    // 出力エンコーディングを設定
    using var outenc = ConsoleWig.OutputEncodingPeriod(Encoding.UTF8);

    // コマンドライン引数をパース
    var options = CliArgs.Parse<Options>(Args);

    // 対象ディレクトリの確定。引数指定が無ければ入力させる。
    var repoPath = options.Target.OmitWhite() ?? ConsoleWig.ReadLine("対象リポジトリ\n>");
    var repoDir = CurrentDir.GetRelativeDirectory(repoPath.CancelIfWhite());

    // 対象ディレクトリの検証
    if (!repoDir.GetRelativeDirectory(".hg").Exists) throw new PavedMessageException("指定されたパスが mercurial リポジトリではありません。");

    // 出力ディレクトリを決定
    var outPath = options.OutDir.OmitWhite() ?? repoDir.Parent?.FullName ?? throw new PavedMessageException("出力ディレクトリを決定できません。");
    var outDir = CurrentDir.GetRelativeDirectory(outPath).WithCreate();

    // 作者名マッピングファイルをコピーして残すかどうか
    var copyAuthorsMap = options.CopyAuthorMap ?? settings.DefaultCopyAuthorMap;

    // 作者名マッピングファイルを手動リネームで準備するか否か
    var manualAuthorRename = options.ManualRename ?? settings.DefaultManualRename;

    // 利用する作者名マッピングファイル
    var usingAuthorMap = options.AuthorMap;

    // タイムスタンプ文字列
    var timestamp = $"{DateTime.Now:yyyyMMdd_HHmmss}";

    // 出力先ディレクトリの存在チェック
    // 増分変換も可能らしいが、このスクリプトでは新規変換だけをサポートすることにする。(現代の状況的に mercurial はもう使わないでほしいので。)
    var gitDir = outDir.GetRelativeDirectory($"{repoDir.Name}-git-{timestamp}");
    if (gitDir.Exists) throw new PavedMessageException($"変換先ディレクトリが既に存在するため処理を中止します。\n  Path='{gitDir.FullName}'");

    // win32mbcs の利用が明示的に指定されていなければ問う
    var useMbcs = options.UseMbcs
        ?? ConsoleWig.ReadLine("【重要】Mercurial リポジトリで win32mbcs が有効でしたか？(yes or no)\n>") switch
        { "y" => true, "yes" => true, "n" => false, "no" => false, _ => throw new PavedMessageException("win32mbcs の利用状況を特定できません。"), };

    // ファイル名エンコーディングの決定
    var fileEnc = options.MbcsEncoding.OmitWhite() ?? settings.DefaultMbcsEncoding;

    // 強制実行フラグが明示的に指定されていなければ問う
    var useForce = options.Force
        ?? ConsoleWig.ReadLine("強制実行を行いますか？(利用する場合は yes)\n>") switch { "y" => true, "yes" => true, _ => false, };

    // キャンセルキーハンドラ
    using var canceller = new CancellationTokenSource();
    using var sigHandlre = ConsoleWig.CancelKeyHandlePeriod(canceller);

    // 作業用一時ディレクトリ作成
    using var tmpDir = new TempDir();

    // git出力用のディレクトリ作成
    gitDir.Create();

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
            var buildFile = ThisSource.GetRelativeDirectory(settings.ImageBuildContext).GetRelativeFile("Dockerfile");
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
        parameter.HostConfig.Mounts.Add(new() { Type = "bind", Source = repoDir.FullName, Target = containerFs.MercurialRepo, ReadOnly = true, });
        parameter.HostConfig.Mounts.Add(new() { Type = "bind", Source = gitDir.FullName, Target = containerFs.GitRepo, });
        parameter.HostConfig.Mounts.Add(new() { Type = "bind", Source = tmpDir.Info.FullName, Target = containerFs.DataDir, });
        return await client.Containers.CreateContainerAsync(parameter, canceller.Token);
    });

    // 変換処理
    var convExecuted = false;
    try
    {
        // コンテナを開始
        var running = await client.Containers.StartContainerAsync(container.ID, new(), canceller.Token);
        if (!running) throw new PavedMessageException($"コンテナの実行を開始できません。");

        // 実行環境にするコンテナ情報生成
        var context = new ContainerContext(client, container.ID);

        // 変換処理で使う作者名マッピングファイル情報。この後の処理でこのパスに準備する。
        var authorMapFile = tmpDir.Info.GetRelativeFile($"authors.txt");

        // 作者名マッピングファイルを準備する。
        if (usingAuthorMap.IsNotWhite())
        {
            // 利用する作者名マッピングファイルが指定されている場合はそれを利用する。
            CurrentDir.GetRelativeFile(usingAuthorMap).CopyTo(authorMapFile.FullName);
        }
        else
        {
            // 作者名マッピングファイルが指定されていない場合、変換元リポジトリの情報から作成する
            // mercurial リポジトリのコミット作者名を取得
            var authors = await Spinner.StartAsync("Mercurial リポジトリのコミット作者情報取得 ...", async spinner =>
            {
                var authorResult = await context.ExecAsync(new[] { "hg", "--repository", containerFs.MercurialRepo, "log", "--template", @"{author}\n", }, cancelToken: canceller.Token);
                if (authorResult.code != 0) throw new PavedMessageException($"Mercurial リポジトリの情報を取得できません。");
                return authorResult.stdout;
            });

            // 作者マッピングファイルを作成
            var authorPat = new Regex(@"^.+<.+>\s*$");
            var authorEditFile = tmpDir.Info.GetRelativeFile($"authors-edit_{timestamp}.txt");
            using (var authorsWriter = authorEditFile.CreateTextWriter(encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                foreach (var hgAuthor in authors.AsTextLines().Distinct().DropWhite())
                {
                    var match = authorPat.Match(hgAuthor);
                    var gitAuthor = match.Success ? hgAuthor : $"{hgAuthor} <{hgAuthor.Replace(" ", "_")}@example.com>";
                    var map = $"{hgAuthor.Quote()}={gitAuthor.Quote()}";
                    authorsWriter.WriteLine(map);
                }
            }

            // 作者名マッピングファイルを関連付けで開いて編集させる
            Console.WriteLine();
            Console.WriteLine("作者名マッピングファイルの編集");
            if (manualAuthorRename)
            {
                // 手動リネームモードの場合、マッピングファイルをカレントディレクトリに持ってくる。
                var cwdMapFile = CurrentDir.GetRelativeFile(authorMapFile.Name).ThrowIfExists(i => new PavedMessageException($"'{i.Name}' が既に存在するため処理継続できません。"));
                var cwdEditFile = CurrentDir.GetRelativeFile("authors-edit.txt").ThrowIfExists(i => new PavedMessageException($"'{i.Name}' が既に存在するため処理継続できません。"));
                authorEditFile.CopyTo(cwdEditFile.FullName);
                // 編集・リネームを待機する。
                Console.WriteLine($"カレントディレクトリに生成された '{cwdEditFile.Name}' を編集し、完了したら '{cwdMapFile.Name}' にリネームしてください。");
                await Spinner.StartAsync("編集完了を待機中 ...", pattern: Patterns.SimpleDots, action: async spinner =>
                {
                    while (cwdEditFile.Exists) { cwdEditFile.Refresh(); await Task.Delay(100, canceller.Token); }   // 編集ファイルが無くなるのを待って、
                    while (!cwdMapFile.Exists) { cwdMapFile.Refresh(); await Task.Delay(100, canceller.Token); }    // リネーム後ファイルが出来るのを待つ。
                                                                                                                    // 変換処理に指定するための場所に移動
                    cwdMapFile.MoveTo(authorMapFile.FullName);
                });
            }
            else
            {
                Console.WriteLine("関連付けによってエディタで作者名マッピングファイルが開かれます。\n必要な更新を行ってエディタを終了してください。");
                await Spinner.StartAsync("編集完了を待機中 ...", pattern: Patterns.SimpleDots, action: async spinner =>
                {
                    // エディタが終了するのを待機
                    using var editor = Process.Start(new ProcessStartInfo(authorEditFile.FullName) { UseShellExecute = true, }) ?? throw new PavedMessageException("エディタを開けません。");
                    var sw = Stopwatch.StartNew();
                    await editor.WaitForExitAsync(canceller.Token);
                    // 終了が早すぎるようであれば異常とみなす
                    // 単一プロセス型のエディタで、起動済みプロセスに情報を渡してすぐに終了するような場合には待機できないのでそれはサポートできない。
                    if (sw.ElapsedMilliseconds < 100) throw new PavedMessageException("関連付けられたエディタプロセスが即時終了しました。この環境では自動処理ができません。");
                    // 変換処理に指定するためのファイル名にリネーム
                    authorEditFile.MoveTo(authorMapFile.FullName);
                });
            }

            // 作者名マッピングファイルを残す設定の場合、コピーする。
            if (copyAuthorsMap)
            {
                var usingAuthorFile = CurrentDir.GetRelativeFile($"authors_{timestamp}.txt");
                authorMapFile.CopyTo(usingAuthorFile.FullName);
            }
        }

        // git リポジトリを作成
        Console.WriteLine();
        await Spinner.StartAsync("Gitリポジトリ初期化 ...", async spinner =>
        {
            var gitInitResult = await context.ExecAsync(new[] { "git", "init", }, workDir: containerFs.GitRepo, canceller.Token);
            if (gitInitResult.code != 0) throw new PavedMessageException($"Git リポジトリを初期化できません。");
            var gitConfigResult = await context.ExecAsync(new[] { "git", "config", "core.ignoreCase", "false", }, workDir: containerFs.GitRepo, canceller.Token);
            if (gitConfigResult.code != 0) throw new PavedMessageException($"Git リポジトリを構成できません。");
        });

        // 変換スクリプトの呼び出し
        var convResult = await Spinner.StartAsync($"リポジトリ変換 to {gitDir.Name} ...", async spinner =>
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
            };
            if (useMbcs) cmdLine.AddRange(new[] { "--fe", fileEnc, });
            if (useForce) cmdLine.Add("--force");
            return await context.ExecAsync(cmdLine, workDir: containerFs.GitRepo, canceller.Token);
        });

        // ここまできたらリポジトリのクリーンナップしないでおくためにフラグ立て
        convExecuted = true;

        // ログ保存
        using (var logWriter = ThisSource.GetRelativeFile($"log_{timestamp}.txt").CreateTextWriter())
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
        if (convResult.code != 0) throw new PavedMessageException($"リポジトリ変換に失敗しました。");

        // 変換後リポジトリの HEAD をチェックアウト
        var checkoutResult = await context.ExecAsync(new[] { "git", "checkout", "HEAD", }, workDir: containerFs.GitRepo, canceller.Token);
        if (checkoutResult.code != 0) throw new PavedMessageException($"変換後リポジトリのワーキングツリーをチェックアウト出来ませんでした。");
    }
    catch (Exception) when (!convExecuted)
    {
        // 変換処理まで進めていなければ作成したgit用ディレクトリの削除の削除を試みる
        try { gitDir.Delete(recursive: true); } catch { }
        throw;
    }
    finally
    {
        // コンテナを破棄
        await client.Containers.RemoveContainerAsync(container.ID, new() { Force = true, });
    }
});
