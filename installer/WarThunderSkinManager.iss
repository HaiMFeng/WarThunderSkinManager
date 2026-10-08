; ============================================================================
; WarThunderSkinManager 安装脚本（Inno Setup 6）
;
; 设计依据：docs/应用自更新设计.md §5（职责清单）、§7.4（桌面快捷方式幂等三前提）
;
; 用法（由 .codebuddy/skills/wtsm-release/scripts/package-release.ps1 调用）：
;   ISCC.exe installer\WarThunderSkinManager.iss ^
;       /DMyAppVersion=0.1.5-dev ^
;       /DMyPayload=<repo>\dist\publish\WarThunderSkinManager.exe ^
;       /O<repo>\dist
;
; 产物：dist\WarThunderSkinManager-Setup-<版本>-win-x64.exe
;
; 三条不可动的约定（动了就会踩坑，见设计文档）：
;   1) AppId 一旦发布**永不修改** —— 它决定"同目录覆盖升级"与卸载项的稳定身份；
;   2) 快捷方式名 = "WarThunder Skin Manager"，与程序里的 ShortcutService.ShortcutName
;      **必须是同一个字符串**（否则会出现两个图标）；
;   3) [UninstallDelete] 里显式删桌面 .lnk —— Inno 只按自己的记录删图标，
;      用户在安装时取消了桌面勾选、之后又用设置页创建的那个否则会残留成孤儿。
; ============================================================================

#ifndef MyAppVersion
  #error 必须用 /DMyAppVersion=<版本> 传入版本号（如 0.1.5-dev）
#endif
#ifndef MyPayload
  #error 必须用 /DMyPayload=<发布产物 exe 路径> 传入 payload
#endif

#define MyAppName "WarThunder Skin Manager"
#define MyAppPublisher "HaiMFeng"
#define MyAppExeName "WarThunderSkinManager.exe"

[Setup]
; ---- 身份（永不变）----
AppId={{B7A4E1C2-6F3D-4A58-9C2B-1D8E5F70A9C3}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}

; ---- 安装位置：每用户、免 UAC（docs/应用自更新设计.md 决策 3）----
; 装到 Program Files 需要提权，而静默更新时的 UAC 会挡住升级 → 默认路径避开它；
; 仍允许用户自选目录（PrivilegesRequiredOverridesAllowed=dialog 会按需提权）。
DefaultDirName={localappdata}\Programs\WarThunderSkinManager
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DisableDirPage=no
DisableWelcomePage=yes
; 不建开始菜单文件夹：不设 DefaultGroupName → 快捷方式直接放在"程序"根下（单应用工具的惯例）
DisableProgramGroupPage=yes
WizardStyle=modern

; ---- 与程序的单实例互斥同名：安装器据此等程序退出后再替换文件（静默更新可靠的关键）----
AppMutex=Local\WarThunderSkinManager.SingleInstance
CloseApplications=yes
RestartApplications=yes

; ---- 外观与体积 ----
OutputBaseFilename=WarThunderSkinManager-Setup-{#MyAppVersion}-win-x64
SetupIconFile=..\WarThunderSkinManager\Assets\app.ico
UninstallDisplayName={#MyAppName} {#MyAppVersion}
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
; payload 已是压缩过的自包含单文件（≈67 MB），Setup 不会再小多少 → 别期待体积下降

; ---- 平台 ----
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
MinVersion=10.0

[Languages]
; 中文界面是**非官方翻译**（Inno Setup 6 不自带 —— 实测它自带的 30 个语言里没有中文）。
; 本仓库已把 `installer\ChineseSimplified.isl`（官方翻译页的 6.5.0+ 版）随脚本一起维护，
; 因此**默认就是中文界面**；下面同时兼容"放在 Inno 安装目录的 Languages\ 下"的情形。
; 两者都没有时退化为英文界面（不影响功能）。
#if FileExists(AddBackslash(CompilerPath) + "Languages\ChineseSimplified.isl")
Name: "chinese"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
#elif FileExists(AddBackslash(SourcePath) + "ChineseSimplified.isl")
Name: "chinese"; MessagesFile: "ChineseSimplified.isl"
#endif
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; **默认勾选**（决策 6）：checkedonce = 仅首次安装默认勾，升级时沿用用户上次的选择
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce

[Files]
; payload = dotnet publish 出的自包含单文件 exe，落地后就是**唯一规范槽位**
Source: "{#MyPayload}"; DestDir: "{app}"; DestName: "{#MyAppExeName}"; Flags: ignoreversion

[Icons]
; 开始菜单：一律创建（{autoprograms} 在管理员模式下自动解析为 common programs）
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
; 桌面：跟随上面的任务勾选；名字与程序里的 ShortcutService.ShortcutName 必须一致
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; 静默安装（应用内自更新）：装完直接回到程序界面
Filename: "{app}\{#MyAppExeName}"; Flags: nowait; Check: WizardSilent
; 交互安装：给"运行程序"勾选（默认勾）
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 桌面快捷方式**兜底删除**（见文件头第 3 条约定）：只删这一个固定名字的文件
Type: files; Name: "{autodesktop}\{#MyAppName}.lnk"

[Code]
const
  ConfigDirName = 'WarThunderSkinManager';

// 配置目录（%LOCALAPPDATA%\WarThunderSkinManager）：**与安装目录并列**，装载与卸载都不碰
function ConfigDirectory(): string;
begin
  Result := ExpandConstant('{localappdata}\') + ConfigDirName;
end;

// 卸载时**询问**是否连带删除配置（默认"否"）：
// 里面是用户的映射 / 部件排除 / 索引 / 预览缓存 —— 属于用户数据，绝不能默默删掉。
// 涂装库（资源目录）与游戏里的激活输出**一律不动**（可能在别的盘、可能几百 GB）。
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    // ⚠️ MsgBox 在 /VERYSILENT 下**仍然会弹**（静默只抑制向导页）→ 会让静默卸载卡住。
    // 静默卸载一律按"保留用户数据"处理（最安全：宁可少删，不可误删）。
    if UninstallSilent then exit;

    if MsgBox('是否同时删除程序配置与预览缓存？' + #13#10 + #13#10 +
              '将删除：' + ConfigDirectory() + #13#10 +
              '（config.json、语言文件、显示名映射、部件排除、索引快照、预览图缓存）' + #13#10 + #13#10 +
              '不会删除：你的涂装库、游戏内的激活输出、以及任何游戏目录里的内容。',
              mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(ConfigDirectory(), True, True, True);
  end;
end;
