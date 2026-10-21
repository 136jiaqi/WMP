# Wand Pulse · GitHub 版

Windows 本机采集 Wand → 校验完整快照 → 上传 GitHub → GitHub Pages 展示 → GitHub Actions 每日钉钉推送。

无需保持 ChatGPT/Codex 在线，不调用 OpenAI API，日常 AI Token 费用为 0。Windows 在采集时需要开机、联网、未休眠，并保持用户登录和 Wand 登录。GitHub 上的日报不负责远程唤醒电脑。

## 交付内容

- `collector/WandPulseGrabber`：基于原项目改造的 .NET 8 采集器。
- `scripts/pipeline.py`：快照校验、XMod 全页扫描、状态缓存、Git 上传；Python 标准库，无第三方包。
- `scripts/run-local.ps1`、`install-task.ps1`：本机入口与任务注册。
- `site/`：可发布到 GitHub Pages 的静态页面，包括榜单、日期/名称/状态筛选、排序、历史趋势和复制。
- `.github/workflows/`：页面发布、每日推送、代码检查。
- `tests/`：真实异常样例、同票数游戏、过期数据、状态回退等回归检查。
- `samples/`：原包历史样例，仅供离线验证；不会作为实时数据自动发布。

## 1. 创建 GitHub 仓库

创建空仓库 `wand-pulse`，默认分支用 `main`。将本目录的全部内容（包括隐藏的 `.github` 目录）放入仓库。

建议把仓库克隆到固定路径，例如 `C:\Projects\wand-pulse`，再复制本包文件进去。后续计划任务依赖这个路径，安装后不要随意移动。请先通过 Git Credential Manager 完成 GitHub 登录并成功手动推送一次；定时任务使用同一个 Windows 用户的凭据。不要把 token 放在远程 URL、配置文件或源码里。

```powershell
git clone https://github.com/YOUR_NAME/wand-pulse.git C:\Projects\wand-pulse
# 将本包内容复制进克隆目录，再执行：
cd C:\Projects\wand-pulse
git add .
git commit -m "Initial Wand Pulse GitHub version"
git push -u origin main
```

如果 Git 尚未配置提交身份，在该仓库配置 `git config user.name "YOUR_NAME"` 和 `git config user.email "你的 GitHub 提交邮箱"`。GitHub 分支规则需允许该用户推送数据更新。每次采集只提交 `site/data/dashboard.json`。

## 2. 启用网页

仓库 Settings → Pages → Build and deployment → Source 选择 **GitHub Actions**。

Actions → **Publish dashboard** → Run workflow。成功后部署结果中显示网页地址。以后本机推送新数据会自动重新部署。

页面发布只上传 `site/`，不会把 `local/`、脚本或凭据作为网站资源发布。网站内容包括投票历史和 XMod 开发状态：仅在这些信息可以公开时启用公开网站；私有仓库不等于网站自动私有，访问控制能力取决于 GitHub 套餐。

## 3. 配置 Windows 采集端

安装 .NET 8 SDK、Python 3.11+（建议 3.12）、Git 和 Wand。Python 安装时加入 PATH。

```powershell
cd C:\Projects\wand-pulse
New-Item -ItemType Directory -Path local -Force
Copy-Item config.example.json local\config.json
```

编辑 `local/config.json`：

- `wandExe`：实际 Wand.exe 路径。
- `dotnetExe`、`pythonExe`、`gitExe`：建议使用可执行文件绝对路径，防止计划任务找不到程序。可以使用现有的 .NET 8 SDK 路径。
- `branch`：默认 `main`，修改时同步修改 `pages.yml` 的分支过滤条件。
- `minGames`：默认 20，防止广告、部分加载或空榜单进入正式数据；这只是结构校验，无法证明每个游戏名称和点数都正确。榜单长期改变时需人工核对后调整。
- `maxDataAgeHours`：默认 6，本机拒绝上传过期或未来时间的数据。

XMod 凭据仅保存在 Windows 用户环境变量，**不进入 GitHub 仓库或 Pages 数据**。请申请新的有效凭据，不要复用原包内的账号凭据。可通过 Windows“编辑账户的环境变量”创建 `XMOD_LOGIN_CREDENTIAL`。也可在交互式 PowerShell 中输入：

```powershell
$value = Read-Host '输入 XMod login-credential' -AsSecureString
$plain = [System.Net.NetworkCredential]::new('', $value).Password
[Environment]::SetEnvironmentVariable('XMOD_LOGIN_CREDENTIAL', $plain, 'User')
Remove-Variable plain,value
```

用户环境变量为本机用户可读配置，不是专用凭据保险库。未配置凭据时仍可采集榜单，但开发状态标为待查询，已有状态保持原时间并提示未刷新。XMod 查询仍依赖原项目内部接口与权限，需真实环境验证。

首次请手动退出 Wand，再带调试端口启动并完成登录：

```powershell
Start-Process "$env:LOCALAPPDATA\Wand\Wand.exe" -ArgumentList '--remote-debugging-port=9222','--remote-debugging-address=127.0.0.1'
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-local.ps1 -NoUpload
# 检查 site/data/dashboard.json 的获取时间、游戏名与客户端一致，再正式运行：
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-local.ps1
```

采集程序不会强制结束 Wand。如果客户端已运行但未开放调试端口，会报错并要求手动退出后重新启动。Windows 开机自动启动的 Wand 也需要相同调试参数，或先关闭其自动启动，让采集脚本启动客户端。

## 4. 设置定时采集

Windows 时区设置为香港/北京（UTC+8），运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\install-task.ps1
```

安装的 `WandPulseGitHub` 任务包含：用户登录时、每日 09:30、从次日 00:15 起每两小时采集。09:30 为 10:00 日报预留状态扫描时间。它使用当前用户的交互会话，注销、关机、休眠时无法保证采集。窗口锁屏时客户端能否稳定工作需在实际机器验证。

每次先采集、校验、扫描 XMod，再提交和推送；整个任务最多 50 分钟。XMod 最多扫描配置指定的页数，任一页失败会保留旧状态，页面和日报提示缓存时间。网络恢复后下一次重试。

查看结果：任务计划程序 → `WandPulseGitHub` → 上次运行结果，`0` 为成功；失败时先在终端手动运行 `run-local.ps1` 查看错误。删除任务：`Unregister-ScheduledTask -TaskName WandPulseGitHub -Confirm:$false`。

仓库被他处修改导致推送拒绝时，先停止任务，确认本地提交，再手动 `git pull --rebase`，解决冲突后 `git push`。程序不会自动强推或丢弃修改。采集仍会保存在 `local/pulse_data.csv`，下次导出包含这些历史。

## 5. 配置每日钉钉推送

仓库 Settings → Secrets and variables → Actions 添加：

| Secret | 内容 |
|---|---|
| `DINGTALK_WEBHOOK` | 新机器人的完整 HTTPS webhook |
| `DINGTALK_SECRET` | 可选：机器人加签密钥，启用加签时必填 |

机器人关键词设置包含“竞品”。如果有 IP 白名单限制，需确认 GitHub 托管 runner 的网络能被接受。

Actions → **Daily DingTalk report** → Run workflow，默认 `dry_run=true`，仅在日志预览。核对后再运行并取消 dry_run，才会发送消息。每日计划任务自动真实发送，不需要 ChatGPT 在线。

计划为 UTC 02:00，即香港/北京 10:00。GitHub 定时工作流可能延迟，不承诺精确到分钟；只读取运行时仓库中最后一次数据，不会等待电脑上线。超过 6 小时没有有效快照会明确发送过期提示。手动重复运行真实推送会重复发消息。不要同时启用原项目的旧钉钉计划任务，否则会收到两份日报。

## 6. 离线检查

```powershell
dotnet build collector\WandPulseGrabber -c Release
python -m unittest discover -s tests -v
# 以下命令会把网页数据替换成历史样例，仅用于本地预览，不要上传为正式数据：
python scripts\pipeline.py export --csv samples\pulse_data.csv --allow-stale
python -m http.server 8000 --directory site
# 浏览器打开 http://localhost:8000
```

样例包含 2026-10-19 的一条广告误抓，导出器会拒绝它，最后可用样例为 2026-10-11 的 24 款游戏。首次真实采集会生成新的正式 JSON。现有生产历史如需导入，可以手动复制原 CSV 到 `local/pulse_data.csv`，真实采集后会追加并重新校验历史。

## 改造与限制

已修正：SPA 等待提前结束、CDP 消息未匹配请求 ID、同票数错误去重、少量异常数据写入、静态导出采用不完整快照、XMod 扫描提前退出遗漏重复条目、缓存优先级阻止状态回退。XMod 刷新改为整轮成功才更新缓存，不再依赖原监控服务的扫描中标记。

不再需要 `WandPulseMonitor` 常驻服务或 5199 端口。历史页面展示的是当前已知开发状态，并非当日状态档案。DOM 解析仍依赖 Wand 界面布局与中文文案，客户端更新后可能需要调整；同票数和数量检查不能代替对客户端的人工验收。

GitHub 自动化额度及 Pages 可用性取决于仓库和套餐。本项目不需要 AI/API 付费；不包含电费、网络、GitHub 超额费用。仓库首次创建、账号认证、Pages 开启、钉钉配置和真实端到端验收需在你的账户环境完成。

参考：[Pages 自定义工作流](https://docs.github.com/en/pages/getting-started-with-github-pages/using-custom-workflows-with-github-pages)、[GitHub 计划事件](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#schedule)。
