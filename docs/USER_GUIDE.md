# GameLingoAI 开发版使用指南

当前已验证开发包：0.1.0.16（2026-10-12）。目前可继续本机开发和验证，尚非正式公开发行版。版本、校验值和未完成的验证见 [发布审查](RELEASE_AUDIT.md)。界面主要使用英文，以下保留按钮名称便于查找。

免安装 ZIP 请完整解压到可写的本地目录，双击根目录 `Start-GameLingoAI.cmd`，不要直接运行 app 中的 EXE。启动器将 `GAMELINGOAI_DATA_ROOT`、TEMP/TMP 设置为本次进程的解压目录 `data` 及其 `temp`；设置、数据库、OCR 模型、草稿和日志都使用该目录。首次关闭程序后，将根目录 `settings.example.json` 复制为 `data/settings.json` 并按下文配置；已有文件不要覆盖。下文 `%LOCALAPPDATA%\GameLingoAI` 的说明指直接启动 EXE 或安装版的默认路径，使用便携启动器时改为 `data`。此设置不迁移旧数据或 Ollama 模型；0.1.0.15 及更早包不支持它。

## 启动与安装

需要 Windows x64、支持 Windows Graphics Capture 的系统，以及 OCR 所需的 x64/AVX CPU、VC++ 和 Media Foundation 组件。应用的 OCR 状态会提示缺失条件；不要从不明网站单独下载 DLL。[OCR 部署说明](OCR_DEPLOYMENT.md)记录具体要求。

开发者从项目根目录构建和启动：

```powershell
dotnet restore GameLingoAI.sln
dotnet build GameLingoAI.sln -c Release --no-restore
dotnet run --project src/GameLingoAI.App -c Release --no-build
```

MSIX 包包含 .NET 运行时；源码运行需要相应开发运行时。当前 `unsigned.msix` 不能当成已签名安装包直接分发。请按 [打包说明](PACKAGING.md)由维护者完成正规签名和信任验证后，通过 Windows 应用安装程序安装；不要关闭签名检查。以前的临时测试证书已经清理，不应重新信任旧测试包。

## 首次配置（先用本地翻译）

1. 关闭应用。若已有 `%LOCALAPPDATA%\GameLingoAI\settings.json`，先备份并编辑它，不要覆盖已有设置；若没有，用仓库 `src/GameLingoAI.App/appsettings.sample.json` 作为新文件模板保存到该路径（必要时创建 GameLingoAI 文件夹）。当前不是全自动首次配置向导。
2. 模板使用本机 Ollama：将 `providers[0].model` 的占位符替换为你已经在 Ollama 安装的模型名；地址为 `http://127.0.0.1:11434/`。应用不会替你安装 Ollama 或下载其翻译模型。
3. 设置 `languages.target`，例如中文使用 `zh-Hans`；`languages.source` 可保留 `auto`。保留 `privacy.remoteTextProcessingConsent: false`，历史与截图开关也先保持关闭。保存合法 JSON。
4. 启动应用，在 Profiles / Settings 查看 OCR 状态，阅读模型来源、大小和许可后，明确选择安装本地 OCR 模型。下载可取消；损坏时使用修复/重新安装，不要绕过 SHA-256 校验。
5. 在 Provider 页面执行 Test connection。它只验证配置/服务，不发送游戏字幕。出现模型缺失时，先在本地翻译服务中安装相应模型。

远程服务配置见 [配置约定](CONFIGURATION.md)及 [自定义 HTTP](CUSTOM_HTTP_PROVIDER.md)。只有在理解字幕文本会发往你选择的服务后，才设置全局 `privacy.remoteTextProcessingConsent: true`。远程请求可能收费，服务方的数据保留政策由你另行确认。

OpenAI-compatible 配置必须明确模型、HTTPS baseUri、`endpointDialect: "chatCompletions"` 和 `secretReference`。密钥不要写入 JSON：通过 Windows 控制面板的“凭据管理器 → Windows 凭据 → 添加普通凭据”，为引用名 `my-provider` 创建地址 `GameLingoAI/my-provider` 的凭据，密码填写密钥，用户名可使用本地标识。配置中的引用仅写 `my-provider`。不要把密钥放在终端参数、截图或聊天记录里。当前应用没有完整的密钥录入向导。

## 开始翻译与停止

1. 在 Home / Session 点击 **Load configured providers and saved profiles**。选定服务；首次可用 **Use ad-hoc regions and global settings**。如选择保存的配置，它有自己的区域和设置，未保存草稿不生效。
2. 到 Window / Region 刷新窗口列表，选择并明确确认要捕获的窗口。配置切换后需重新确认，不会按窗口标题自动开始捕获。
3. 启动预览，只框选字幕区域，添加/调整区域并保存需要复用的草稿。不要选择聊天、账号或付款界面。优先使用窗口化或无边框窗口模式。
4. 回到 Home / Session，点击 **Start**。查看状态和错误提示；字幕稳定出现后才会翻译。首次本地模型加载可能较慢。
5. **Pause / Resume** 暂停/恢复；**Stop** 停止会话；**Emergency stop** 请求紧急停止并隐藏字幕。单独 Hide subtitles 只隐藏字幕，不代表停止捕获或请求。
6. 快捷键在 Profiles / Settings 配置，每次启动都要明确 **Apply for this run**；只保存不会注册。冲突时换组合，不要关闭系统安全功能。

覆盖字幕通常不接受鼠标点击；调整位置请使用 **Edit overlay layout**。切到其他前台应用时覆盖层应隐藏，返回所选窗口后恢复。目标关闭、最小化或失效时应查看恢复提示，必要时重新选择并确认。

## 隐私与本地数据

- 捕获图片供本机预处理/OCR，不发送给翻译服务。远程翻译发送识别文本和必要提示上下文；本地模型下载会连接官方模型托管站点，但不上传屏幕。
- 历史默认关闭，**翻译缓存仍会保存文本**，两者独立。可把缓存最大行数设为 0 并 Apply retention 禁用存储；清除既有内容需明确执行清理。
- `%LOCALAPPDATA%\GameLingoAI` 包含设置、数据库、模型及日志；密钥另存 Windows 凭据管理器。数据库/备份并非加密保险箱，不应公开分享。
- Diagnostics 的支持包先预览、确认再保存，不自动上传。普通缓存内容导出、恢复数据库副本可能包含私密文本，与支持包不同。
- 要清理缓存和历史，先停止会话，在本地数据库/隐私区域勾选确认，再执行 **Clear private data and compact**。配置、术语表及单独备份保留。

## 升级、卸载与回退

升级前停止会话并关闭应用，备份完整本地数据目录，避免只复制正在使用的 SQLite 主文件而遗漏 WAL。新包应保持正式包身份/发布者一致且版本更高；核验签名与发布方提供的最终哈希再升级。

通过 Windows 设置 → 应用卸载。卸载保留用户数据和凭据，避免丢失配置/模型；不要以卸载成功推断私密数据已清除。需要完全移除时，先决定是否保留备份，再手动清理确切的 GameLingoAI 数据目录及其 `GameLingoAI/` 凭据，不要删除整个 LocalAppData 或其他应用的数据。

不要直接用旧版本打开已经升级的数据库。回退应关闭应用、保留当前数据副本，由维护者部署兼容的已签名旧版本并恢复升级前的完整备份。没有兼容备份时，停止回退并请求维护支持，不要强制改数据库版本号。

## 常见问题与已知限制

| 现象 | 检查方法 |
|---|---|
| could not start | 关闭重复实例，确认使用正确构建/安装产物、运行时和有效配置；记录版本与提示。不要反复下载 DLL 或删除数据库 |
| Start 不可用 | 先加载并选择服务，确认窗口和有效区域；查看 OCR/配置验证状态 |
| 无字幕或识别不全 | 确认捕获目标、区域、OCR 模型/语言、置信度及字幕是否稳定；已通过 12 项中英日合成普通/小字/描边识别，但复杂背景和特殊字体尚未全面验证 |
| 拖动/切屏后位置异常 | 停止并重新确认窗口；记录两屏分辨率/缩放和版本。已测 125%/100%，不能代表全部组合 |
| 连接失败/401/429 | 检查服务地址、已安装模型、凭据引用、授权和额度；不要把密钥贴入诊断报告 |
| 离线 | 本地 Ollama 可在模型和服务就绪时离线工作；已有缓存只覆盖命中的内容，没有专用“仅缓存”开关，不能承诺断网后所有字幕都可翻译 |
| 数据库错误 | 停止会话，保留原文件；使用恢复副本功能并交由维护者检查，不要自动重建覆盖数据 |
| 截图里没有覆盖字幕 | 单个目标窗口截图不一定包含独立覆盖窗口；目视检查显示情况，勿为截图关闭隐私保护 |

不支持音频翻译、独占全屏保证、注入/游戏内存访问、绕过反作弊或自动操作游戏。干净 Windows 10/11、完整缩放/睡眠恢复矩阵、长时间原生性能和完整无障碍验收尚未完成，因此现在不应宣称已达到正式发布标准。
