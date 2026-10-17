#!/usr/bin/env bash
# PageShot — 把 MFAAvalonia 页面离屏渲染成 PNG（不创建窗口、不抢前台、不占任务栏）。
# fork 自用工具，不随发布包分发；说明见本目录 README.md。
#
# 用法（在仓库根目录执行）：
#   tools/PageShot/pageshot.sh                    # 抽卡页 → 本目录 out/ps-gacha.png
#   tools/PageShot/pageshot.sh activity           # 活动页
#   tools/PageShot/pageshot.sh share              # 导出晒卡的分享卡（固定深色设计稿，高度自适应）
#   tools/PageShot/pageshot.sh activity dark      # 深色主题
#   tools/PageShot/pageshot.sh gacha light 2      # 2 倍分辨率
#   tools/PageShot/pageshot.sh -- --width 800 --height 600
#   tools/PageShot/pageshot.sh -- --help          # PageShot 的全部参数
#
# 位置参数简写是「页面 [主题] [缩放]」；带 -- 的参数原样透传给 PageShot。
# 数据目录默认 G:/MBCCtools工作区（真实数据），用 --data-root 覆盖。
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"

DOTNET="${DOTNET:-$USERPROFILE/.dotnet/dotnet.exe}"
[ -x "$DOTNET" ] || DOTNET="dotnet"

PAGE="gacha"
THEME="Light"
SCALE="1"
DATA_ROOT="${DATA_ROOT:-G:/MBCCtools工作区}"
PASSTHRU=()
HAS_DATA_ROOT=0

EXPECT_VALUE=0
EXPECT_KIND=""
while [ $# -gt 0 ]; do
    if [ "$EXPECT_VALUE" = 1 ]; then
        # 上一个参数是 --xxx，这个无论长什么样都是它的值（哪怕是 "activity"）
        PASSTHRU+=("$1")
        EXPECT_VALUE=0
        # 透传的 --page/--theme 也要让输出文件名跟着走，否则会拿 gacha 的名字存 activity 的图
        case "$EXPECT_KIND" in
            page) PAGE="$1" ;;
            theme) THEME="$1" ;;
        esac
        EXPECT_KIND=""
    else
        case "$1" in
            --)
                # 只是分隔符，丢掉即可（后面的参数按下面规则继续收集）
                ;;
            --data-root)
                PASSTHRU+=("$1")
                EXPECT_VALUE=1
                HAS_DATA_ROOT=1
                ;;
            --page)
                PASSTHRU+=("$1")
                EXPECT_VALUE=1
                EXPECT_KIND="page"
                ;;
            --theme)
                PASSTHRU+=("$1")
                EXPECT_VALUE=1
                EXPECT_KIND="theme"
                ;;
            --*)
                PASSTHRU+=("$1")
                EXPECT_VALUE=1
                ;;
            -h)
                PASSTHRU+=("$1")
                ;;
            gacha|gacharecord|activity|activities|share|sharecard)
                PAGE="$1"
                ;;
            light|Light|dark|Dark)
                THEME="$1"
                ;;
            1|1.5|2|3)
                SCALE="$1"
                ;;
            *)
                PASSTHRU+=("$1")
                ;;
        esac
    fi
    shift
done

# 出图落在工具自己的 out/ 下，工具目录之外不留东西
mkdir -p "$HERE/out"
OUT="$HERE/out/ps-$PAGE.png"
if [ "$(echo "$THEME" | tr '[:upper:]' '[:lower:]')" = "dark" ]; then
    OUT="$HERE/out/ps-$PAGE-dark.png"
fi

# 没显式给 --data-root 就用默认的真实数据目录（MBCCtools 项目根）
if [ "$HAS_DATA_ROOT" = 0 ]; then
    PASSTHRU=(--data-root "$DATA_ROOT" "${PASSTHRU[@]}")
fi

echo "构建 PageShot…"
"$DOTNET" build "$HERE/PageShot.csproj" -c Debug -v q --nologo >/dev/null

"$DOTNET" "$HERE/bin/Debug/net10.0/PageShot.dll" \
    --page "$PAGE" --theme "$THEME" --scale "$SCALE" --out "$OUT" \
    ${PASSTHRU[@]+"${PASSTHRU[@]}"}
