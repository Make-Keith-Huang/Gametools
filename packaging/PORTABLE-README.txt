GameLingoAI — Windows x64 开发试用版

这不是正式发布验收完成的版本。未签名，Windows 可能提示未知发布者；
不要关闭系统安全检查。完整无障碍、干净系统、远程服务和长时间验收尚未完成。
这是项目所有者明确选择发布的开发预览，不是稳定版或合规认证。
原生第三方组件的归属审查仍有缺口；许可证和审查状态随包保留，
发布此预览不表示这些缺口已经解决，也不改变第三方许可要求。
已知性能问题：云端合成回放 CPU 耗时 515.625/546.875 ms，超过
500 ms 门槛；所有者允许带此问题发布预览，性能验收仍未通过。

一、启动与数据目录
1. 将整个 ZIP 解压到可写的本地目录。不要在压缩包内运行，也不要只复制 EXE。
   本项目所有者请使用 D:\ai工作数据和目录存放点\GameLingoAI 下的独立目录。
2. 双击 Start-GameLingoAI.cmd。它设置仅本次进程生效的数据与临时目录，
   然后打开 app\GameLingoAI.App.exe。不需要安装或信任测试证书。
3. 数据位于本目录的 data：settings.json、gamelingo.db、region-draft.json、
   models\ocr、logs 和 temp。不要直接双击 app 内的 EXE，否则默认使用 LocalAppData。
4. 不会自动迁移旧数据。更换目录或升级前应关闭程序，完整备份 data，
   不要只复制正在使用的 SQLite 主文件，不要用旧版本打开已升级数据库。
5. Ollama 自己的模型和 Windows 凭据不在上述目录内；启动器不会迁移它们。

二、首次设置
1. 首次启动后关闭程序，将 settings.example.json 复制为 data\settings.json。
   已有设置时不要覆盖。模板中的 model 必须改为已安装的模型名，
   例如确实安装了 qwen2.5:3b 时才填写该名称。
2. 中文翻译将 languages.target 改为 zh-Hans。保留远程文本许可 false。
3. 本地翻译需要另行安装并启动 Ollama，安装所选翻译模型。
   本程序不自带 Ollama/翻译模型，不会自动下载它们。
4. 在 Profiles / Settings 中检查并明确安装 OCR 模型。需要相应 Windows、
   VC++、Media Foundation 和 x64/AVX 支持；模型下载会校验 SHA-256。
5. Provider 页面测试连接；Home / Session 加载并选择服务；
   Window / Region 选择并明确确认窗口，框选字幕区域，再返回点击 Start。
6. Pause 暂停，Stop 停止，Emergency stop 紧急停止。Hide subtitles 只隐藏字幕。

三、隐私与限制
只捕获你确认的窗口，图片仅在本地做 OCR；远程翻译需要明确同意发送文字。
没有音频、注入、游戏内存读取、反作弊绕过或游戏自动操作。
历史默认关闭，但翻译缓存仍可能保存文本。data 不要上传到 GitHub。
密钥不能填写在配置、聊天或日志中；使用 Windows 凭据管理器。
本地模型首次响应可能较慢；复杂字体、多屏/系统组合并非都已验证。
遇到错误请记录版本和提示，不要反复删除数据、下载不明 DLL 或关闭安全检查。

四、升级和删除
关闭程序后再升级或备份。删除程序目录会一并删除其中 data，
若需保留配置、数据库或 OCR 模型，请先备份；不要删除其他目录或系统缓存。
ZIP 没有安装程序，不修改证书信任，不注册系统服务。

五、许可证
原创项目代码采用 LICENSE.txt 中的 MIT 许可证。
第三方组件与模型保留各自许可，见 app\THIRD-PARTY-NOTICES.txt、
app\licenses 和 app\sbom.cdx.json。MIT 不覆盖这些第三方许可。
app\release-status.json 明确保留尚未完成的公开发布审查状态。
