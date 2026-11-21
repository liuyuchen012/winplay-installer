# 搭建本机 Android 编译环境

本项目是 `net10.0-android`（.NET Android，非 MAUI UI），要用本机编译出 APK，
需要 4 样东西。全部装完约 8~12 GB，首次配置耗时 20~40 分钟。

> 不想装？用 GitHub Actions 云端出包即可，见文末「方案 B」。
> 本机装齐的唯一好处是：秒级编译、`adb install` 直接装机、`adb logcat` 抓真机日志。

---

## 一、要装什么

| # | 组件 | 为什么需要 | 体积 |
|---|---|---|---|
| 1 | .NET 10 SDK | 提供 `dotnet` 命令与 `net10.0` 目标框架 | ~300 MB |
| 2 | android workload | `net10.0-android` 的编译目标，缺它直接报 NETSDK1147 | ~1 GB |
| 3 | JDK 17 | 打包 APK 要调 `javac` / `keytool` / `apksigner` | ~200 MB |
| 4 | Android SDK + platform-tools | 提供 `android.jar`、`build-tools`、`adb` | ~5 GB |

**注意**：本项目没有 MAUI 界面，只需要 `android` workload，**不要**装 `maui-android`（体积大且会增加冲突风险）。

---

## 二、安装步骤（PowerShell，逐条执行）

### 1. 装 .NET 10 SDK 与 JDK 17

```powershell
winget install Microsoft.DotNet.SDK.10
winget install Microsoft.OpenJDK.17
```

装完**关掉当前终端重新打开**，让 PATH 生效。

### 2. 装 android workload

```powershell
dotnet workload install android
```

这一步会联网下载约 1 GB，慢是正常的。若报 manifest 过期：

```powershell
dotnet workload update
dotnet workload install android
```

### 3. 装 Android SDK

先装 adb：

```powershell
winget install Google.PlatformTools
```

再装 SDK 命令行工具（**任选其一**）：

- **路线 A：只装命令行工具（省空间）**
  去 https://developer.android.com/studio#command-line-tools-only 下载 Windows 版 zip，
  解压到 `C:\Android\Sdk\cmdline-tools\latest`（必须是这个层级），然后：

  ```powershell
  $env:ANDROID_HOME = "C:\Android\Sdk"
  & "$env:ANDROID_HOME\cmdline-tools\latest\bin\sdkmanager.bat" --licenses
  & "$env:ANDROID_HOME\cmdline-tools\latest\bin\sdkmanager.bat" "platform-tools" "platforms;android-36" "build-tools;36.0.0"
  ```

  `--licenses` 会连续问你是否接受，一路输入 `y` 回车。

- **路线 B：装 Android Studio（省心但占空间）**

  ```powershell
  winget install Google.AndroidStudio
  ```

  打开一次 Studio → SDK Manager → 勾选 `Android 16 (API 36)` + `Android SDK Build-Tools 36` + `Platform-Tools`。

### 4. 设置环境变量

```powershell
[Environment]::SetEnvironmentVariable("ANDROID_HOME", "C:\Android\Sdk", "User")
[Environment]::SetEnvironmentVariable("JAVA_HOME", (Get-ChildItem "C:\Program Files\Microsoft\jdk-17*" -Directory | Select-Object -First 1).FullName, "User")
```

上面第二条会自动找到 JDK 17 的真实目录名，不用手抄版本号。
然后**重新打开终端**。

---

## 三、验证是否装好（4 条命令）

```powershell
dotnet --list-sdks     # 应出现 10.0.x
dotnet workload list   # 应出现 android
adb version            # 应显示 Android Debug Bridge version 1.0.41 之类
java -version          # 应显示 17.x
```

四条全过才算装齐。任何一条报「找不到命令」，说明 PATH 没生效，重开终端再试。

---

## 四、构建 APK

在项目根目录（有 `WinPlayInstaller.csproj` 的那一层）执行：

```powershell
dotnet build -c Release -f net10.0-android
```

产物：

```
bin\Release\net10.0-android\com.winplay.installer-Signed.apk
```

### 装到手机

手机开「开发者选项 → USB 调试」，插线后：

```powershell
adb devices                              # 确认能看到设备
adb install -r .\bin\Release\net10.0-android\com.winplay.installer-Signed.apk
```

`-r` 表示覆盖安装、保留数据。若提示 `INSTALL_FAILED_UPDATE_INCOMPATIBLE`（签名冲突）：

```powershell
adb uninstall com.winplay.installer
adb install .\bin\Release\net10.0-android\com.winplay.installer-Signed.apk
```

### 抓运行日志（排查游戏起不来时必用）

```powershell
adb logcat -c                                        # 先清空
adb logcat | Select-String -Pattern "winplay|wine|DOTNET"
```

只清空再抓，能避免历史日志干扰。

---

## 五、常见报错对照表

| 报错 | 原因 | 解决 |
|---|---|---|
| `NETSDK1045: 当前 .NET SDK 不支持 net10.0` | 没装 .NET 10 SDK，或装了旧版本 | `winget install Microsoft.DotNet.SDK.10` |
| `NETSDK1147: 找不到 workload 'android'` | 没装 workload | `dotnet workload install android` |
| `XA5300: Android SDK 未找到` | `ANDROID_HOME` 没设或路径错 | 重设环境变量并重开终端 |
| `XA5207: 找不到 JDK` | `JAVA_HOME` 没设或指向 JDK 8/11 | 装 JDK 17 并设 `JAVA_HOME` |
| `XA0030: 需要 Java 17` | JDK 版本太老 | 换 JDK 17 |
| `error MSB3644: 找不到参考程序集` | 只装了 .NET SDK 没装 workload | 装 android workload |
| Visual Studio 打不开项目 / `Build.BuildSolution 不可用` | VS 2022 17.14 对 `net10.0-android` 支持不完整 | **用命令行构建**，不要依赖 VS |
| `adb: 无法将"adb"项识别为...` | platform-tools 不在 PATH | `winget install Google.PlatformTools` 后重开终端 |
| 安装时 `INSTALL_FAILED_NO_MATCHING_ABIS` | 项目只编了 arm64 | 本项目 `RuntimeIdentifiers=android-arm64`，需 arm64 设备 |

---

## 六、每次改完代码要做什么

```powershell
# 1. 改动后先清缓存再构建（项目 README 明确要求，否则偶发 UnsatisfiedLinkError）
Remove-Item -Recurse -Force .\bin, .\obj -ErrorAction SilentlyContinue
dotnet build -c Release -f net10.0-android

# 2. 装到手机
adb install -r .\bin\Release\net10.0-android\com.winplay.installer-Signed.apk

# 3. 抓日志
adb logcat | Select-String -Pattern "winplay|wine"
```

---

## 七、方案 B：不装本机，用云端编译

本仓库已配置 `.github/workflows/build-apk.yml`，推送代码即自动出包：

1. 改完代码 `git push origin main`
2. 打开仓库 **Actions** 页，等 `build-apk` 变绿（约 5~10 分钟）
3. 进入该次运行，页面底部 **Artifacts** → 下载 `winplay-installer-apk` → 解压得 APK

代价：不能 `adb logcat` 抓真机日志，「工作目录是否生效」这类问题只能在手机上靠应用内日志判断
（清单项写 `"args": "cmd /c cd > D:\\cwd.txt"`，跑完看文件内容）。

---

## 八、安装完成后需要确认的三件事

装齐工具链后，第一次构建前建议先确认：

1. `dotnet workload list` 里 `android` 的状态不是 `[not installed]`
2. `dotnet --list-sdks` 只有 10.0.x（若同时有 9.x，`global.json` 缺失时可能选错，用 `dotnet --version` 在项目目录确认）
3. `adb devices` 能看到你的设备且状态是 `device` 而不是 `unauthorized`（`unauthorized` 需在手机上点「允许 USB 调试」）
