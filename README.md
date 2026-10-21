# PCL Unlock

项目须知：仅为 PCL2 CE 提供地区绕过启动。**不支持原版 PCL2。** 本工具为注入式工具，不修改 PCL2 CE 本体和 Windows 系统区域设置。

## 下载

[Releases下载](https://github.com/Luckyumimi/PCL_Unlock/releases)

[网盘下载](https://sachimi.lanzouu.com/itlQS4cvs7gj) 
**密码：gb5a**


## 使用

1. 将 PCL_Unlock.exe 放到 PCL2 CE 主程序所在目录并运行。
2. 双击启动 PCL_Unlock.exe ，选择 PCL2_CE*.exe ，随后会自动在桌面创建 PCL2CE(unlock) 快捷方式
3. 后续启动，直接双击桌面快捷方式即可，PCL2CE 后续未修改检测点时，理论一直可用


## 运行行为

- 需要管理员权限，因为目标 PCL2 CE 可能带有“以管理员身份运行”兼容性标记。
- 启动钩子优先使用启动器同目录下的 Hook/PclRegionHook.dll，否则从单文件内嵌资源释放到 %LOCALAPPDATA%/PCLRegionUnlock/Hook/。同目录可放置 Hook/0Harmony.dll 供本地 Hook 使用。
- 日志文件输出到 .\PCLRegionUnlock.launcher.log。

## 原理与边界

通过 .NET DOTNET_STARTUP_HOOKS 在启动的 PCL2 CE 进程中加载 PclRegionHook.dll。Hook 覆盖 Windows DeviceRegion 读取、PCL-CE 区域信号读取及相关受限状态属性。每次启动的 Hook 结果写入 PCL_Unlock.exe 同目录的 PCLRegionUnlock.log；如果 Hook 被单独加载且没有启动器传入目录，则回退到 Hook 所在目录。


## 构建

.\build.ps1 可构建到 dist；

.\build.ps1 -Deploy 可构建并部署；

.\build.ps1 -Deploy -DeployPath D:\game\PCL2 指定部署目录。

## To PCL2CE

如果你们注意到了这个项目，希望手下留情，上有政策下有对策