# convert-hg-to-git.csx

Docker と hg-fast-export を利用して Mercurial リポジトリを Git リポジトリに変換する補助スクリプト。  
C#スクリプトで作成しており、基本的に日本語の Windows で Docker Desktop の環境を想定している。  

## 実行環境

以下の準備が必要となる。

- .NET 7 SDK のインストール
    - https://dotnet.microsoft.com/download
- dotnet-script v1.4.0 以降のインストール
    - `dotnet tool install -g dotnet-script`
- Docker Desktop for Windows のインストール


## 使い方

基本的にはただ実行すれば必要情報の入力を求めるプロンプトを表示する。  
(Windows 環境で `dotnet script register` を行い、関連付けから実行することを想定している。)  

```shell
dotnet script ./convert-hg-to-git.csx 
```

これは以下のように動作する。
1. プロンプトを表示して Mercurial リポジトリパスの入力を求める。
1. リポジトリでの win32mbcs の使用有無の入力を求める。
1. 強制実行モードを利用するかの入力を求める。
    - 初回は使用せずに  hg-fast-export からの警告を確認すべき。
    - しかしある程度の規模のリポジトリだと強制モードが必要な場合が多い。
1. Dockerイメージが無ければビルドする。
1. 変換に使う作者名マッピングファイルを生成し関連付けによって開く。
    - マッピングは左辺が Mercurial コミット作者。右辺が Git 変換時の作者名。
    - 必要な編集・保存を行いエディタを閉じる。エディタを終了すると自動で次に進む。
1. 変換処理が実行される。
    - win32mbcs を使用していた場合、ファイル名のエンコーディングが cp932 (ShiftJIS) であるとして変換処理を呼び出す。  
    (このため日本語環境が対象となっている。)
    - 変換先は元リポジトリと同じ場所に `-git-yyyyMMdd_HHmmss` のようなサフィックスのフォルダが作られる。
1. 処理が終了するとスクリプトディレクトリにログを出力する。
    - 成否にかかわらず。

パラメータ無しで実行した際に問われる内容はコマンドラインパラメータにて指定することが可能。  
```
dotnet script ./convert-hg-to-git.csx d:\temp\repo --win32mbcs true --force false
```

その他のパラメータについては以下またはスクリプト内を直接確認のこと。  
```
dotnet script ./convert-hg-to-git.csx -- --help
```
