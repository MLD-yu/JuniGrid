; Inno Setup script for JuniGrid v1.0.1 (Chinese)
; Packages the self-contained build (publish/sc) into a Windows installer.

#define MyAppName "JuniGrid"
#define MyAppVersion "1.1.0"
#define MyAppPublisher "JuniGrid"
#define MyAppExeName "JuniGrid.exe"

[Setup]
AppId={{7E1B2C64-9A4D-4C0E-9F61-3A5D8B2C4E10}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoDescription=JuniGrid Your helper for Stardew Valley
DefaultDirName={autopf}\JuniGrid
DisableDirPage=no
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=dist-tmp
OutputBaseFilename=JuniGrid-cn-v{#MyAppVersion}-setup
Compression=lzma2/fast
SolidCompression=no
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
SetupIconFile=JuniGrid\Assets\junigrid-logo.ico
UninstallIconFile=JuniGrid\Assets\junigrid-logo.ico

[Languages]
Name: "chinese"; MessagesFile: "compiler:Default.isl"

[Messages]
SetupAppTitle=JuniGrid 安装
SetupWindowTitle=JuniGrid 安装 — v{#MyAppVersion}
ButtonBack=< 上一步(&B)
ButtonNext=下一步(&N) >
ButtonInstall=安装(&I)
ButtonOK=确定
ButtonCancel=取消
ButtonFinish=完成(&F)
ButtonBrowse=浏览(&B)...
ButtonWizardBrowse=浏览(&R)...
ButtonNewFolder=新建文件夹(&M)
ClickNext=点击“下一步”继续，或点击“取消”退出安装。
WelcomeLabel1=欢迎使用 [name] 安装向导
WelcomeLabel2=这将把 [name/ver] 安装到您的计算机。%n%nJuniGrid — 您的星露谷物语小助手。%n%n建议您在继续之前关闭所有其他应用程序。
SelectDirDesc=应将 [name] 安装到哪里？
SelectDirLabel3=安装程序将把 [name] 安装到以下文件夹。
SelectDirBrowseLabel=点击“下一步”继续。如果想选择其他文件夹，请点击“浏览”。
DiskSpaceMBLabel=至少需要 [mb] MB 的可用磁盘空间。
WizardSelectTasks=选择附加任务
SelectTasksDesc=需要执行哪些附加任务？
SelectTasksLabel2=选择安装 [name] 时要执行的附加任务，然后点击“下一步”。
AdditionalIcons=附加快捷方式:
CreateDesktopIcon=创建桌面快捷方式(&D)
WizardReady=准备安装
ReadyLabel1=安装程序已准备好在您的计算机上安装 [name]。
ReadyLabel2a=点击“安装”继续安装，或点击“上一步”检查或更改设置。
ReadyMemoDir=目标位置:
ReadyMemoTasks=附加任务:
WizardInstalling=正在安装
InstallingLabel=请稍候，安装程序正在您的计算机上安装 [name]。
ExtractingLabel=正在解压文件...
FinishedHeadingLabel=完成 [name] 安装向导
FinishedLabel=安装程序已在您的计算机上完成 [name] 的安装。您可以通过选择已安装的快捷方式来启动应用程序。%n%n点击“完成”以退出安装。
ExitSetupTitle=退出安装
ExitSetupMessage=安装尚未完成。如果现在退出，程序将不会被安装。%n%n您可以稍后再次运行安装程序以完成安装。%n%n确定要退出吗？
ApplicationsFound=以下应用程序正在使用需要由安装程序更新的文件。建议允许安装程序自动关闭这些应用程序。
ApplicationsFound2=以下应用程序正在使用需要由安装程序更新的文件。建议允许安装程序自动关闭这些应用程序。安装完成后，安装程序将尝试重新启动这些应用程序。
CloseApplications=自动关闭这些应用程序(&A)
DontCloseApplications=不关闭这些应用程序(&D)
ErrorCloseApplications=安装程序无法自动关闭所有应用程序。建议您在继续之前，手动关闭所有正在使用这些文件的应用程序。
BrowseDialogTitle=浏览文件夹
BrowseDialogLabel=请在下方列表中选择一个文件夹，然后点击确定。
NewFolderName=新建文件夹
SelectStartMenuFolderDesc=安装程序应把程序的快捷方式放在哪里？
SelectStartMenuFolderLabel3=安装程序将在以下“开始菜单”文件夹中创建程序的快捷方式。
SelectStartMenuFolderBrowseLabel=点击“下一步”继续。如果想选择其他文件夹，请点击“浏览”。
WizardPreparing=正在准备安装
PreparingDesc=安装程序正在准备在您的计算机上安装 [name]。
WizardSelectDir=选择安装位置

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式:"; Flags: checkedonce

[Files]
Source: "publish\sc\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "运行 {#MyAppName}"; Flags: nowait postinstall

[UninstallDelete]
; WebView2 user-data folder and logs created at runtime inside the install dir
Type: filesandordirs; Name: "{app}\userdata"

[Code]
procedure InitializeWizard();
begin
  WizardForm.Color := clWhite;
  WizardForm.WelcomePage.Color := clWhite;
  WizardForm.FinishedPage.Color := clWhite;
  WizardForm.WelcomeLabel2.Color := clWhite;
  WizardForm.WelcomeLabel2.Font.Color := clBlack;
  WizardForm.FinishedLabel.Font.Color := clBlack;
  WizardForm.PageNameLabel.Font.Color := $0019943C;   // logo green #19943C (BGR) accent
  WizardForm.PageDescriptionLabel.Font.Color := clBlack;
end;
