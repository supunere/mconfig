# Release build

Windows 11 x64向けの自己完結型ビルドは、.NETランタイムの追加インストールなしで起動できます。
ZIP内のすべてのファイルとdataフォルダーを一緒に展開してください。

```powershell
dotnet publish src/Mconfig.App/Mconfig.App.csproj -c Release -r win-x64 --self-contained true -p:Version=0.1.0 -p:RuntimeFrameworkVersion=10.0.12 -o artifacts/win-x64
Copy-Item LICENSE,THIRD-PARTY-NOTICES.md,README.md,RELEASE.md -Destination artifacts/win-x64
Compress-Archive -Path artifacts/win-x64/* -DestinationPath artifacts/mconfig-win-x64.zip
```

配布にはLICENSE、THIRD-PARTY-NOTICES.md、README.md、RELEASE.mdと同梱サンプルを含めてください。
上記のランタイム版は、依存ライセンス文書の対象に合わせています。
依存パッケージやランタイムのバージョンを更新したときは、著作権表示とライセンス本文も更新してください。
