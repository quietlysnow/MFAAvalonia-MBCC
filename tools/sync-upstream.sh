#!/usr/bin/env bash
#
# 把上游 MaaXYZ/MFAAvalonia 的 master 同步进本 fork 的定制分支（默认取当前分支）。
#
# 与 .github/workflows/upstream-sync.yml 的分工：
#   云端 workflow —— 每天定时检测上游，能干净合并就自动合并并推回本仓库；
#   本脚本        —— 在本地对齐云端合并的结果、补上云端还没合的部分，并按需构建/部署。
# 先「快进到 myfork 上的合并结果」再补合，是为了让合并提交只有一份，避免云端和本地各合一次而分叉。
#
# 用法：
#   tools/sync-upstream.sh               同步（fetch 上游 + 对齐 myfork + 合并），只在本地提交
#   tools/sync-upstream.sh --check       只报告是否落后上游；落后时退出码 1（可拿去做通知/定时）
#   tools/sync-upstream.sh --push        同步成功后把结果推回 myfork
#   tools/sync-upstream.sh --build       同步成功后 publish 到 publish/
#   tools/sync-upstream.sh --deploy      同步 + publish + 部署到 MBCCtools 根目录
#   tools/sync-upstream.sh --deploy-only 不碰 git、不重新构建，把现有 publish/ 部署过去
#   tools/sync-upstream.sh --branch 名字  指定要同步的分支（默认当前分支，且必须已检出）
#
#   --build / --deploy 即使上游没更新也会执行（想重新构建部署时直接用）。
#
# 环境变量：
#   MBCC_ROOT        部署目标（默认 G:/MBCCtools工作区，须含 interface.json）
#   UPSTREAM_REMOTE  上游远程名（默认自动找 URL 里带 MaaXYZ/MFAAvalonia 的那个）
#   FORK_REMOTE      自己 fork 的远程名（默认自动找 URL 里带 MFAAvalonia-MBCC 的那个）
#
set -euo pipefail

UPSTREAM_SLUG="MaaXYZ/MFAAvalonia"
UPSTREAM_BRANCH="master"
FORK_SLUG="MFAAvalonia-MBCC"
MBCC_ROOT_DEFAULT="G:/MBCCtools工作区"

check_only=0
deploy_only=0
do_push=0
do_build=0
do_deploy=0
merged=0
branch=""

die() { printf '✗ %s\n' "$*" >&2; exit 2; }
info() { printf '%s\n' "$*"; }

usage() {
    cat <<'EOF'
用法：tools/sync-upstream.sh [选项]

  （无选项）       fetch 上游 + 对齐 myfork + 合并；只在本地提交，不推送
  --check          只报告落后上游多少（落后 → 退出码 1）
  --push           同步成功后推回 myfork
  --build          同步成功后 publish 到 publish/
  --deploy         同步 + publish + 部署到 MBCCtools 根目录
  --deploy-only    不碰 git、不重新构建，把现有 publish/ 直接部署过去
  --branch 名字    指定分支（默认当前分支，必须已检出）
  -h, --help       这段帮助

环境变量：MBCC_ROOT、UPSTREAM_REMOTE、FORK_REMOTE（见脚本头部注释）
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
        --check) check_only=1 ;;
        --push) do_push=1 ;;
        --build) do_build=1 ;;
        --deploy) do_deploy=1; do_build=1 ;;
        --deploy-only) deploy_only=1; do_deploy=1 ;;
        --branch)
            branch="${2:-}"
            [ -n "$branch" ] || die "--branch 后面要跟分支名"
            shift
            ;;
        -h | --help) usage; exit 0 ;;
        *) die "不认识的参数：$1（--help 看用法）" ;;
    esac
    shift
done

if [ "$deploy_only" -eq 1 ] && { [ "$do_build" -eq 1 ] || [ "$check_only" -eq 1 ] || [ "$do_push" -eq 1 ]; }; then
    die "--deploy-only 只负责部署现有 publish/，不能和 --build / --deploy / --check / --push 一起用"
fi

root=$(git rev-parse --show-toplevel 2>/dev/null) || die "当前目录不在 git 仓库里"
cd "$root"

build() {
    local dotnet_bin
    if [ -x "$USERPROFILE/.dotnet/dotnet.exe" ]; then
        dotnet_bin="$USERPROFILE/.dotnet/dotnet.exe" # §5：PATH 上的 dotnet 可能是 9.x，太老
    elif command -v dotnet >/dev/null 2>&1; then
        dotnet_bin="dotnet"
    else
        die "找不到 .NET SDK（期望 $USERPROFILE/.dotnet/dotnet.exe，或 PATH 上有 dotnet）"
    fi
    info "→ publish（$dotnet_bin）"
    "$dotnet_bin" publish MFAAvalonia.Desktop/MFAAvalonia.Desktop.csproj \
        -c Release -r win-x64 --self-contained false -o publish
}

# 部署守的都是 AGENTS.md §5 那两个坑：只覆盖构建产物、不让根目录残留 MFAAvalonia.Core.dll 遮蔽 libs/
deploy() {
    local root="${MBCC_ROOT:-$MBCC_ROOT_DEFAULT}" f
    local missing=()
    [ -f "$root/interface.json" ] || die "「$root」下没有 interface.json，不像 MBCCtools 项目根；用 MBCC_ROOT=<路径> 指定"
    for f in MFAAvalonia.exe MFAAvalonia.dll libs/MFAAvalonia.Core.dll; do
        [ -e "publish/$f" ] || missing+=("publish/$f")
    done
    [ "${#missing[@]}" -eq 0 ] || die "publish/ 里缺构建产物（${missing[*]}），先跑 --build"
    if [ -e "$root/MFAAvalonia.Core.dll" ]; then
        die "$root/MFAAvalonia.Core.dll 还在：它会遮蔽 libs/ 里的新版本，表现是「改了代码界面没变」（AGENTS.md §5 坑 2）。先删掉：rm '$root/MFAAvalonia.Core.dll'"
    fi
    if tasklist //FI "IMAGENAME eq MFAAvalonia.exe" //NH 2>/dev/null | grep -qi "MFAAvalonia.exe"; then
        die "MFAAvalonia.exe 正在运行，先关掉再部署"
    fi

    info "→ 部署到 $root（只覆盖构建产物，config/、record/、resource/ 一律不动）"
    local items=(MFAAvalonia.exe MFAAvalonia.dll MFAAvalonia.deps.json MFAAvalonia.runtimeconfig.json libloader.dll libs runtimes MaaAgentBinary)
    for f in "${items[@]}"; do
        if [ -e "publish/$f" ]; then
            rm -rf "$root/$f"
            cp -r "publish/$f" "$root/$f"
            info "    ↑ $f"
        else
            info "    ！publish/$f 不存在，跳过（上游删了它？）"
        fi
    done
    [ -f "$root/libs/MFAAvalonia.Core.dll" ] || die "部署后 $root/libs/MFAAvalonia.Core.dll 不存在，检查 publish/libs/"
    info "✓ 部署完成：$root/MFAAvalonia.exe"
}

# 只部署现有 publish/：不 fetch、不合并，所以放最前面
if [ "$deploy_only" -eq 1 ]; then
    deploy
    exit 0
fi

branch="${branch:-$(git rev-parse --abbrev-ref HEAD)}"
[ "$branch" != "HEAD" ] || die "当前是 detached HEAD，用 --branch 指定分支"
[ "$(git rev-parse --abbrev-ref HEAD)" = "$branch" ] || die "当前不在 $branch 上，先 git checkout $branch"

# 上游和 fork 的远程名都从 URL 里认，免得写死（本仓库 origin = 上游，myfork = 自己的 fork）
find_remote() {
    local r url
    for r in $(git remote); do
        url=$(git remote get-url "$r" 2>/dev/null || true)
        case "$url" in *"$1"*) printf '%s' "$r"; return 0 ;; esac
    done
    return 1
}
upstream_remote="${UPSTREAM_REMOTE:-$(find_remote "$UPSTREAM_SLUG" || true)}"
fork_remote="${FORK_REMOTE:-$(find_remote "$FORK_SLUG" || true)}"
[ -n "$upstream_remote" ] || die "找不到指向 $UPSTREAM_SLUG 的远程，可用 UPSTREAM_REMOTE=<名字> 指定"

fw_version() { sed -n 's/.*Maa\.Framework\.Runtimes" Version="\([^"]*\)".*/\1/p' MFAAvalonia/MFAAvalonia.csproj | sed -n '1p'; }
app_version() { sed -n 's/.*return "\(v[0-9][^"]*\)";.*/\1/p' MFAAvalonia/ViewModels/Windows/RootViewModel.cs | sed -n '1p'; }

# 合并失败就把冲突列清楚、把工作区还原，绝不留下半截的合并状态
merge_or_bail() {
    local ref="$1" conflicts
    if git merge -m "chore: 同步上游（自动，$ref）" "$ref"; then
        merged=1
        return 0
    fi
    conflicts=$(git diff --name-only --diff-filter=U | sort -u || true)
    git merge --abort || true
    printf '✗ 合并 %s 有冲突，已放弃（工作区回到合并前）。冲突文件：\n' "$ref" >&2
    printf '%s\n' "$conflicts" | sed 's/^/    /' >&2
    printf '  手动跑 `git merge %s` 解决；参照 AGENTS.md §3/§7：多集中在 3 个注册点文件，通常「双方都保留」。\n' "$ref" >&2
    exit 2
}

if [ "$check_only" -eq 0 ] && [ -n "$(git status --porcelain --untracked-files=no)" ]; then
    die "有未提交的改动，先 commit 或 stash 再同步（免得合并把在改的东西卷进去）"
fi

info "→ 取上游 $upstream_remote/$UPSTREAM_BRANCH"
git fetch --quiet "$upstream_remote" "$UPSTREAM_BRANCH"
if [ -n "$fork_remote" ] && ! git fetch --quiet "$fork_remote" "$branch" 2>/dev/null; then
    info "  （$fork_remote/$branch 还取不到，跳过对齐）"
fi

behind=$(git rev-list --count "HEAD..$upstream_remote/$UPSTREAM_BRANCH")

if [ "$check_only" -eq 1 ]; then
    if [ "$behind" -eq 0 ]; then
        info "✓ 已是最新：上游 $UPSTREAM_BRANCH 没有新提交（本分支独有 $(git rev-list --count "$upstream_remote/$UPSTREAM_BRANCH..HEAD") 个提交）"
        exit 0
    fi
    info "↓ 落后上游 $behind 个提交："
    git log --oneline --no-decorate "HEAD..$upstream_remote/$UPSTREAM_BRANCH" | sed 's/^/    /'
    exit 1
fi

# 先跟 myfork 对齐：云端自动合并的结果是共享的，本地不要另起一份
fork_ref="$fork_remote/$branch"
if [ -n "$fork_remote" ] && git rev-parse --verify --quiet "$fork_ref" >/dev/null; then
    if [ "$(git rev-parse HEAD)" != "$(git rev-parse "$fork_ref")" ]; then
        if git merge-base --is-ancestor HEAD "$fork_ref"; then
            info "→ 快进到 $fork_ref（云端自动同步的结果）"
            git merge --ff-only "$fork_ref"
        elif git merge-base --is-ancestor "$fork_ref" HEAD; then
            info "→ 本地比 $fork_ref 新，跳过对齐"
        else
            info "→ 本地与 $fork_ref 各有提交，合并云端结果"
            merge_or_bail "$fork_ref"
        fi
    fi
fi

behind=$(git rev-list --count "HEAD..$upstream_remote/$UPSTREAM_BRANCH")
if [ "$behind" -gt 0 ]; then
    fw_before=$(fw_version)
    app_before=$(app_version)
    info "→ 合并 $upstream_remote/$UPSTREAM_BRANCH（$behind 个提交）："
    git log --oneline --no-decorate "HEAD..$upstream_remote/$UPSTREAM_BRANCH" | sed 's/^/    /'
    merge_or_bail "$upstream_remote/$UPSTREAM_BRANCH"
    info "✓ 合并完成"
    [ "$app_before" = "$(app_version)" ] || info "  应用版本：$app_before → $(app_version)"
    if [ "$fw_before" != "$(fw_version)" ]; then
        info "  ⚠ 框架版本：$fw_before → $(fw_version) —— MBCCtools 侧 pip install MaaFw==<新版本> 要一起动，否则 Custom 节点连不上（AGENTS.md §7）"
    fi
else
    info "✓ 相对上游已是最新（本分支独有 $(git rev-list --count "$upstream_remote/$UPSTREAM_BRANCH..HEAD") 个提交）"
fi

if [ "$do_push" -eq 1 ]; then
    [ -n "$fork_remote" ] || die "找不到指向 $FORK_SLUG 的远程，推不了"
    info "→ 推送 $branch → $fork_remote"
    git push "$fork_remote" "HEAD:refs/heads/$branch"
elif [ "$merged" -eq 1 ] && [ -n "$fork_remote" ]; then
    info "  提示：本次合并只在本地；加 --push 推回 $fork_remote，云端下次运行就不会再合一份。"
fi

[ "$do_build" -eq 1 ] && build
[ "$do_deploy" -eq 1 ] && deploy
exit 0
