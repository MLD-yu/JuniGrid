# SignPath 开源项目签名申请材料

> 用途：提交 https://signpath.io/ 的 **Free code signing for open source projects** 申请。
> 申请表单是英文的，本文件正文可直接复制；中文是给你自己看的说明。
> 提交前把 `{...}` 占位符替换成实际值。

---

## 一、申请表单要填的核心信息

| 表单字段 | 填什么 |
|---|---|
| Project name | `JuniGrid` |
| Repository URL | `https://github.com/MLD-yu/JuniGrid` |
| License | `GNU General Public License v3.0`（仓库根 `LICENSE`） |
| Primary language | `C#`（.NET 10 / WPF + Blazor Hybrid） |
| Project website | `https://github.com/MLD-yu/JuniGrid` |
| Maintainer | `MLD (GitHub: MLD-yu)` |
| Contact email | 你的邮箱 |
| Artifact to sign | `JuniGrid-cn-v<version>-setup.exe`（单文件安装包） |
| Build system | GitHub Actions（`.github/workflows/build.yml`） |

---

## 二、正文（英文，可直接粘贴）

### What the software does

JuniGrid is a desktop mod manager and launcher for **Stardew Valley**, a
single-player farming game. It is a Windows application built with .NET 10
(WPF hosting a Blazor WebView2 UI).

Its users are players who install community-made mods. Before JuniGrid, that
meant manually unzipping archives into the game folder, hand-editing JSON
manifests, and tracking which mod needs which dependency. JuniGrid does the
boring parts: browse mods from Nexus Mods, download them under the user's own
Nexus account, resolve dependencies, install into the right folders, and switch
the game between versions.

The application is free, has no advertising, no telemetry, no bundled
third-party offers, and no paid tier. It is maintained by one person.

### Why it needs code signing

Two independent problems, both caused by the absence of a signature:

1. **Windows SmartScreen** shows "unrecognised app" on every download. Users
   are taught by that warning to click through security prompts, which is
   exactly the habit we do not want to teach them.

2. **Antivirus false positives.** Several engines flag the installer with
   heuristic or machine-learning names (for example
   `Trojan:Win32/Wacatac.B!ml`). An unsigned, compressed, self-extracting
   single-file installer with an embedded payload looks structurally similar to
   a dropper, and with no certificate there is nothing to weigh against that
   resemblance. Users who hit this either abandon the software or, worse,
   disable their antivirus to run it.

A code signing certificate lets the file be evaluated on the identity of its
publisher rather than on its shape alone.

### Build reproducibility

The build is fully scripted and runs in CI:

- `.github/workflows/build.yml` — GitHub Actions, `windows-latest`
- `installer/build-installer.ps1` — the whole pipeline, one entry point
- `global.json` pins the .NET SDK feature band

Pipeline, in order:

1. `dotnet publish JuniGrid/JuniGrid.csproj` — self-contained, win-x64
2. `installer/PayloadTool` packs that output into `payload.lz`
   (custom container `JGP1`: file table + single-stream LZMA, SHA-256 verified
   by the packer after compressing)
3. `dotnet publish installer/JuniGridInstaller` — single-file installer with
   `payload.lz` embedded as a resource
4. The resulting `JuniGridSetup.exe` is renamed to
   `JuniGrid-cn-v<version>-setup.exe` and its SHA-256 is recorded

No step downloads or executes anything from an unverified source during the
build. The three third-party binaries that get bundled are fetched from their
official publishers and their Authenticode signatures are checked before they
are allowed into the payload (see the `Verify bundled dependencies` step).

### One deliberate difference between the CI artifact and a local release build

The CI artifact is the file we submit for signing, so this needs stating
plainly rather than being discovered during review.

`smapi-bridge/JuniGridCommandBridge.csproj` is a small SMAPI mod that ships
next to the app. It targets .NET 6 and — deliberately — does not use implicit
framework references; instead it compiles against the 244 assemblies in the
game's own install directory (the game is self-contained, so `System.Runtime.dll`
and friends are right there). That means it can only be built on a machine that
has the game installed.

`JuniGrid.csproj` therefore decides whether to build it based on whether the
game directory exists (`BuildBridgeMod`, with `-p:BuildBridgeMod=true/false`
to force either way). On CI there is no game, so the bridge mod is **not built**
and the artifact does not contain `bridge-mod/`. At runtime the app degrades
gracefully: `CommandBridgeService.CopyBridgedInto` reports
"安装包里缺少命令桥文件" and the rest of the app is unaffected.

This is a functional reduction, not a security-relevant difference. The
artifact we ask to be signed is exactly the CI artifact — we will upload that
exact file, and the SHA-256 in the submission is its hash.

Builds are **not** byte-for-byte reproducible: .NET embeds a PE timestamp and
build paths, so a local rebuild of the same commit produces a different hash.
That is why the file submitted for signing is the CI artifact rather than a
locally rebuilt one.

The current CI artifact:

```

File    : JuniGrid-cn-v1.2.9-setup.exe
Size    : 122,517,782 bytes
SHA-256 : 15253a7248f98f6ae50460c433e7341106a4e97e09730769a6f6015ac8478d5a
Commit  : f88b55435c5c0290cbec099e4f0ebe3d536f25bd
Run     : https://github.com/MLD-yu/JuniGrid/actions/runs/39607961140

```

### Behaviours a reviewer will notice, and what each is for

These are the things that make the installer look unusual. All of them are
intentional and documented in the source.

**1. `nxm://` URI handler registration.**
The app writes `HKCU\Software\Classes\nxm` so the "Mod Manager Download" button
on Nexus Mods reaches it. Receiving that link is the product's core function.
If another mod manager (Vortex, NMM) was registered there first, its original
command line is backed up under `HKCU\Software\JuniGrid` and restored verbatim
when the user turns the feature off. Only `HKCU` is written — no `HKLM`, and no
file association outside this one scheme.

**2. Terminating processes named `JuniGrid`.**
During install/upgrade, and when a second copy is launched while the first is
not responding, so that files held open by the previous instance can be
replaced. These are the app's own earlier instances; the process name is the
application's own.

**3. Bundled third-party components.**
Each is run only when actually needed:

- `tools\WebView2\MicrosoftEdgeWebView2Setup.exe` — only if the Microsoft Edge
  WebView2 runtime is absent; the UI is built on that runtime.
- `tools\XnaRedist\xnafx40_redist.msi` — only for game versions that need XNA
  Framework 4.0. Its Microsoft signature is verified before it is run.
- `tools\DepotDownloader\DepotDownloader.exe` — only after the user signs in to
  their own Steam account inside the app, to fetch their own game files. This
  one is not signed by its upstream project (SteamRE/DepotDownloader); that is
  a known upstream situation, not something this project introduced.

**4. Delayed self-deletion on uninstall.**
Uninstall schedules removal of its own executables after the process exits,
because a running executable cannot delete itself on Windows. This is the one
behaviour most likely to match a malware heuristic; see the note below.

### What we are changing

Two of the above have been removed or narrowed rather than explained away:

- The uninstall self-delete no longer runs `cmd.exe /c del ... & rd ...` at
  all. That command line is a well-known malware pattern and we would rather
  not ship it even though ours is benign. (Note: `MoveFileEx` with
  `MOVEFILE_DELAY_UNTIL_REBOOT` cannot be used here — it requires
  administrator rights and the app is deliberately `asInvoker`.) Instead, the
  uninstaller spawns one short-lived copy of itself with
  `--purge-install-dir <dir>`, which deletes exactly two known file names and
  removes the directory only if it is empty. No shell, no command-line
  deletion verbs, no process-name kills.
- Third-party archives downloaded at runtime from GitHub mirrors are now
  verified against the SHA-256 digest published by the upstream release,
  fetched from the GitHub Release API. If no official digest is reachable the
  download still proceeds but is recorded in the app log as unverified.

### Licensing

GPL-3.0. Full text in `LICENSE`. The project has no CLA and no dual licensing;
contributions are accepted under the same license.

---

## 三、提交前的确认清单

**1. 仓库根要有 `LICENSE`** ✅ GPL-3.0 官方全文（SHA-256 与 gnu.org 一致）

**2. README 的许可段要改** ✅ 已改为 GPL-3.0 并补版权声明

**3. CI 要能真的跑通一次** ✅ 已通，4 轮迭代

| run | 结果 | 修了什么 |
|---|---|---|
| 1 | Build installer 失败（3 秒） | 我编辑 `build-installer.ps1` 时弄丢 UTF-8 BOM，PowerShell 5.1 在 CP1252 runner 上把中文注释里的破折号解成弯引号，解析器被骗 |
| 2 | 全绿 | 补回 BOM + CI 改用 pwsh 直接调用 |
| 3 | 全绿，Total 498 | 加了 security 改动 |
| 4 | 全绿，Total 527 | 加了 29 条新用例，全部被发现并执行 |

**4. 送签文件用 CI 产物，不要本机重建** ⚠️ 这条自己别搞错

理由：.NET 构建不逐字节可复现（PE 时间戳、路径），本机重建的哈希必然不同。
而且本机有游戏目录 ⇒ 会额外编出 `bridge-mod/`，与 CI 产物不是同一种东西。

**做法**：把 artifact 下载下来，上传这一份；表里填它的 SHA-256。别本机重建。

---

## 四、审核大概会问什么

按我对这类申请的预期（不是保证）：

| 可能的问题 | 准备怎么答 |
|---|---|
| "为什么捆绑未签名的 DepotDownloader？" | 它是用户登录 Steam 后才需要的组件，上游本身不签名。已考虑改成按需下载，但会让用户多下 32 MB，暂时保留并在此说明。 |
| "为什么有 cmd.exe 自删？" | 已移除（见 "What we are changing"）：现在卸载收尾由自身子实例完成，不再起 shell。 |
| "项目活跃度？" | 30+ 提交、多个已发布版本、521 个单元测试。 |
| "谁在维护？" | 单人项目，GitHub 账号 MLD-yu。 |
| "有没有商业动机？" | 无。免费、无广告、无遥测、无付费版。 |

---

## 五、通过之后

SignPath 会给你一个 workflow 片段，大致是：

```yaml
- uses: signpath/github-action-submit-signing-request@v1
  with:
    api-token: ${{ secrets.SIGNPATH_API_TOKEN }}
    organization-id: ...
    project-slug: junigrid
    signing-policy-slug: release-signing
    artifact-configuration-slug: exe
    github-artifact-id: ...
    wait-for-completion: true
```

接进 `.github/workflows/build.yml` 的 `Collect artifacts` 之后即可。届时签出来的
exe 带 OV 证书，卡巴斯基和 SmartScreen 都会走"看证书"那条路，不再纯靠猜。
