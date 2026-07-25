# QQAntiRecall

[![CI](https://github.com/HinataYoki/QQAntiRecall/actions/workflows/ci.yml/badge.svg)](https://github.com/HinataYoki/QQAntiRecall/actions/workflows/ci.yml)

面向 Windows x64 QQ NT 的防撤回补丁管理工具。
下载后只需运行一个 `QQAntiRecall.exe`。程序会离线检查 QQ 当前版本和待更新版本，
确认补丁位置安全后安装，并提供经过哈希校验的备份、恢复与清理功能。

> 仅在你有权管理的设备和 QQ 安装上使用。QQ 更新可能改变内部代码；无法完整识别时，
> 程序会拒绝修改，不会猜测偏移或强行安装。

## 支持范围

- 系统：Windows x64
- 客户端：QQ NT x64

## 兼容性

补丁同时处理普通撤回、无痕撤回和撤回通知更新，用于覆盖群聊与私聊撤回路径。当前三组
特征码已在以下 QQ 版本完成只读扫描验证：

- `9.9.33-51552`
- `9.9.33-51728`

这些版本号不是安装白名单。程序只扫描 `versions/config.json` 中的 `curVersion` 和
`readyVersion`，并根据实际文件判定是否可以操作：

| 扫描结果 | 程序行为 |
| --- | --- |
| 三组原始特征码均唯一匹配 | 允许安装 |
| 三组补丁特征码均唯一匹配 | 识别为已安装 |
| 特征码缺失、重复或原始/补丁状态混合 | 禁止写入并显示原因 |

因此，其他 QQ 版本在特征码仍完整匹配时也可以安装，但不代表已经完成真实群聊和私聊验证。
每次 QQ 更新后都应重新确认实际行为。

## 使用

1. 启动 `QQAntiRecall.exe`。程序会自动查找 QQ，也可以手动选择包含 `QQ.exe` 和
   `versions` 的安装目录。
2. 查看扫描结果，确认当前版本和待更新版本均处于可安装状态。
3. QQ 未运行时，选择“安装补丁”。
4. QQ 正在运行时，选择“关闭 QQ · 安装并重启”；确认后程序会关闭 QQ、安装补丁并从
   已验证的安装目录重新启动 QQ。
5. 需要撤销补丁时，完全退出 QQ，重新扫描后选择“恢复备份”。

“安装补丁”或“恢复备份”显示灰色时，以界面状态提示为准。常见原因包括 QQ 正在运行、
没有完全匹配的备份、版本不受支持或多个目标处于混合状态。即使已经找到备份，QQ 运行时
也不会允许恢复。

## 备份与恢复

- 每次真正安装一组新目标前都会创建独立备份；已完整安装时重复点击不会新增备份。
- 恢复后仍会保留备份，所以 QQ 升级或多次“恢复后重新安装”会增加磁盘占用。
- 左侧“备份存储”提供打开目录和清理按钮。清理前会显示数量与大小并要求确认。
- 清理只删除当前 QQ 目录的旧版本备份和哈希完全相同的更早重复副本。
- 当前可恢复备份、其他 QQ 安装目录的备份和无法识别的目录不会被清理。

默认备份目录：

```text
%LOCALAPPDATA%\QQAntiRecall\backups
```

不要手工修改备份文件或 `manifest.json`，否则哈希校验失败后程序会拒绝恢复。

## 安全设计

- 安装前要求每个目标中的三组原始特征码分别且仅匹配一次。
- 修改前保存原文件、SHA-256 和目标清单；当前版本与待更新版本作为一个事务处理。
- 写入前再次检测 QQ 和目标文件，避免预检后发生变化。
- 任一目标替换失败时回滚已修改目标；回滚失败时保留 `.rollback` 恢复文件。
- 恢复前同时校验备份原文件、当前补丁文件和清单中的 SHA-256。
- 不注入 QQ 进程，不加载第三方 DLL，也不在线下载补丁或特征码。

## 开发与发布

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。

```powershell
dotnet restore .\QQAntiRecall.sln
dotnet build .\QQAntiRecall.sln --configuration Release -warnaserror
dotnet test .\QQAntiRecall.sln --configuration Release --no-build
dotnet run --project .\src\QQAntiRecall.App\QQAntiRecall.App.csproj
```

生成 Windows x64 单文件：

```powershell
dotnet publish .\src\QQAntiRecall.App\QQAntiRecall.App.csproj -c Release -r win-x64 --self-contained true -o .\artifacts\win-x64
```

CI 会执行格式检查、完整测试、单文件发布和启动冒烟测试。当前产物未进行代码签名，正式
公开分发前应补充 Windows 代码签名。

## 技术来源

整体离线补丁与恢复思路参考
[huiyadanli/RevokeMsgPatcher](https://github.com/huiyadanli/RevokeMsgPatcher)。QQ NT 的
三组特征码和补丁字节参考
[AQiaoYo/NTQQAntiRecall](https://github.com/AQiaoYo/NTQQAntiRecall) 的固定提交
[`32b6178a62dec99466db2a88ba8fa6d57450bf9c`](https://github.com/AQiaoYo/NTQQAntiRecall/commit/32b6178a62dec99466db2a88ba8fa6d57450bf9c)。
上游来源链路还包括
[NapNeko/NTQQAntiRecall](https://github.com/NapNeko/NTQQAntiRecall)。

## 许可

[MIT](LICENSE)
