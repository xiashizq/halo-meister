#!/usr/bin/env python3
import json
from pathlib import Path

I18N = Path(__file__).resolve().parents[1] / "src" / "HaloMeister.App" / "Assets" / "i18n"

EXTRA = {
    "en": {
        "bridge.summary_stale": "The game is running bridge v{0} but this build ships v{1}. Choose Repair / update bridge in the header, then restart the game.",
        "bridge.summary_ready": "Ready. Bridge v{0} responded at {1}.",
        "bridge.summary_installed_not_running": "Installed, but the game bridge is not running. Start or restart the game.",
        "bridge.summary_running_install_missing": "The bridge is running at {0}, but its install could not be located.",
        "bridge.summary_not_installed": "The UE4SS scripting bridge is not installed.",
    },
    "zh-Hans": {
        "bridge.summary_stale": "游戏正在运行 bridge v{0}，但本程序附带 v{1}。请在顶部选择“修复 / 更新 bridge”，然后重启游戏。",
        "bridge.summary_ready": "就绪。Bridge v{0} 于 {1} 响应。",
        "bridge.summary_installed_not_running": "已安装，但游戏内 bridge 未运行。请启动或重启游戏。",
        "bridge.summary_running_install_missing": "Bridge 于 {0} 正在运行，但找不到其安装位置。",
        "bridge.summary_not_installed": "未安装 UE4SS scripting bridge。",
    },
    "ja": {
        "bridge.summary_stale": "ゲームは bridge v{0} を実行中ですが、このビルド同梱は v{1} です。ヘッダーで「修復 / 更新 bridge」を選び、ゲームを再起動してください。",
        "bridge.summary_ready": "準備完了。Bridge v{0} が {1} に応答しました。",
        "bridge.summary_installed_not_running": "インストール済みですが、ゲーム内 bridge が動作していません。ゲームを起動または再起動してください。",
        "bridge.summary_running_install_missing": "Bridge は {0} に動作中ですが、インストール場所を特定できません。",
        "bridge.summary_not_installed": "UE4SS scripting bridge がインストールされていません。",
    },
    "ko": {
        "bridge.summary_stale": "게임은 bridge v{0}을(를) 실행 중이지만 이 빌드에는 v{1}이(가) 포함되어 있습니다. 헤더에서 '복구 / 업데이트 bridge'를 선택한 뒤 게임을 다시 시작하세요.",
        "bridge.summary_ready": "준비됨. Bridge v{0}이(가) {1}에 응답했습니다.",
        "bridge.summary_installed_not_running": "설치되어 있지만 게임 내 bridge가 실행되고 있지 않습니다. 게임을 시작하거나 다시 시작하세요.",
        "bridge.summary_running_install_missing": "Bridge가 {0}에 실행 중이지만 설치 위치를 찾을 수 없습니다.",
        "bridge.summary_not_installed": "UE4SS scripting bridge가 설치되어 있지 않습니다.",
    },
}

for lang, extras in EXTRA.items():
    path = I18N / f"{lang}.json"
    data = json.loads(path.read_text(encoding="utf-8"))
    data.update(extras)
    ordered = dict(sorted(data.items(), key=lambda kv: kv[0]))
    path.write_text(json.dumps(ordered, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(path.name, len(ordered))
