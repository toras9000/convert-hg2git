@echo off

setlocal 

set script_path=%~dp0
set convert_script=%script_path%/convert-hg-to-git.csx

echo win32mbcs / ShiftJIS の利用が前提として強制モードで変換実行
dotnet script "%convert_script%" -- ^
    --win32mbcs true ^
    --default-branch main ^
    --file-enc cp932 ^
    --force true ^
    --copy-authors true

pause

