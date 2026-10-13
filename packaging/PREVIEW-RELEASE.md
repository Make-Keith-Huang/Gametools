# GameLingoAI 0.1.0.17 — Windows x64 免安装开发预览

下载附件 `GameLingoAI-0.1.0.17-win-x64-preview.zip`，不要把 GitHub 自动生成的 Source code ZIP 当成程序。

## 使用方法

1. 解压整个 ZIP 到可写的本地文件夹，双击 `Start-GameLingoAI.cmd`。
2. 首次启动后关闭程序，按包内 `README.txt` 配置 `data/settings.json`；不要覆盖已有设置。
3. 在程序中明确下载 OCR 模型。本地翻译还需自行安装 Ollama 和翻译模型，或配置受支持的远程翻译服务并同意发送文字。
4. 选择并确认要捕获的窗口和字幕区域，再启动翻译。

Windows x64，需要相应 VC++ 运行库、Media Foundation 与 CPU 指令集支持。自包含 .NET，不要求另装 .NET SDK。模型、API 密钥和个人数据库不包含在包内。本包不是开箱即用的翻译模型合集。

## 重要限制

- **开发预发布版，未签名，不是正式验收完成的稳定版。** 不要关闭 Windows 安全检查或安装来源不明的 DLL。
- **已知性能问题：** 云端 40 帧合成回放的 CPU 耗时两次测得 515.625 ms / 546.875 ms，超过 500 ms 门槛；性能验收未通过。项目所有者明确允许将这一单项问题保留后发布预览。原始失败记录见 [首次检查](https://github.com/Make-Keith-Huang/Gametools/actions/runs/38285312873) 和 [复核](https://github.com/Make-Keith-Huang/Gametools/actions/runs/38384470330)。不调高门槛，不将普通 CI 的失败改成通过；发布流程只允许这一项已知失败，其他检查仍阻止发布。
- 完整干净 Windows 10/11、多屏缩放、无障碍、远程服务与长时间测试尚未全部完成。自动测试、包完整性和短时启动检查不能替代这些验收。
- 第三方原生组件的归属/分发审查仍不完整，包括 OpenCV 非自由构建选项和 Paddle 部分静态依赖来源。这是已知未决事项；本次预览发布不宣称已解决。包内保留 LICENSE、THIRD-PARTY-NOTICES、licenses、SBOM、native-provenance 与 release-status；不将审查标记改为通过。原创代码的 MIT 不覆盖第三方条款。
- 图片只在本地 OCR；仅在明确同意远程翻译后发送文字。模型首次响应可能较慢；不要将 data 目录作为问题附件上传。
- 升级前关闭应用并完整备份 data；删除目录会同时删除其中的用户数据。

附件中的 `portable-hash.json` 提供 ZIP 的 SHA-256。此版本由对应源码提交重新构建，不是把旧 ZIP 改名。
