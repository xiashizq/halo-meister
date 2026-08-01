#!/usr/bin/env python3
"""Apply missing ja/ko translations for keys where zh-Hans differs from en."""

from __future__ import annotations

import json
import re
from pathlib import Path

I18N_DIR = Path(__file__).resolve().parents[1] / "src" / "HaloMeister.App" / "Assets" / "i18n"

# Keys: zh-Hans != en, ja/ko still == en at time of authoring.
TRANSLATIONS: dict[str, dict[str, str]] = {
    "config.backed_up_config_files": {
        "ja": "{0} 個の設定ファイルを {1} にバックアップしました。",
        "ko": "{0}개 설정 파일을 {1}에 백업했습니다.",
    },
    "config.binding_suffix": {
        "ja": "{0} バインディング",
        "ko": "{0} 바인딩",
    },
    "config.borderless_fullscreen": {
        "ja": "ボーダーレス全画面",
        "ko": "테두리 없는 전체 화면",
    },
    "config.category_accessibility": {
        "ja": "アクセシビリティ",
        "ko": "접근성",
    },
    "config.category_audio_voice": {
        "ja": "オーディオとボイス",
        "ko": "오디오 및 음성",
    },
    "config.category_controller": {
        "ja": "コントローラー",
        "ko": "컨트롤러",
    },
    "config.category_gameplay_camera": {
        "ja": "Gameplay とカメラ",
        "ko": "Gameplay 및 카메라",
    },
    "config.category_gameplay_modifiers": {
        "ja": "Gameplay 修飾子",
        "ko": "Gameplay 수정자",
    },
    "config.category_keyboard_mouse": {
        "ja": "キーボードとマウス",
        "ko": "키보드 및 마우스",
    },
    "config.category_other": {
        "ja": "その他",
        "ko": "기타",
    },
    "config.category_subtitles": {
        "ja": "字幕",
        "ko": "자막",
    },
    "config.category_video": {
        "ja": "ビデオ",
        "ko": "비디오",
    },
    "config.category_window_layout": {
        "ja": "ウィンドウレイアウト",
        "ko": "창 레이아웃",
    },
    "config.create_or_select_backup": {
        "ja": "先にバックアップを作成するか選択してください。",
        "ko": "먼저 백업을 만들거나 선택하세요.",
    },
    "config.custom_modifier_preset_n": {
        "ja": "カスタム修飾子プリセット {0}",
        "ko": "사용자 지정 수정자 프리셋 {0}",
    },
    "config.desc_collapsed": {
        "ja": "ImGui ウィンドウが折りたたまれた状態で起動するかどうか。",
        "ko": "ImGui 창이 접힌 상태로 시작할지 여부.",
    },
    "config.desc_custom_input_mapping_gamepad": {
        "ja": "各ゲームパッド操作のバインディング。",
        "ko": "각 게임패드 동작 바인딩.",
    },
    "config.desc_custom_input_mapping_kbm": {
        "ja": "各キーボードとマウス操作のバインディング。",
        "ko": "각 키보드 및 마우스 동작 바인딩.",
    },
    "config.desc_field_of_view": {
        "ja": "一人称の水平視野。",
        "ko": "1인칭 수평 시야.",
    },
    "config.desc_field_of_view_3rd_person": {
        "ja": "三人称の視野。",
        "ko": "3인칭 시야.",
    },
    "config.desc_frame_rate_limit": {
        "ja": "最大レンダリングフレームレート。0 または -1 は通常無制限を意味します。",
        "ko": "최대 렌더링 프레임 속도. 0 또는 -1은 보통 무제한을 의미합니다.",
    },
    "config.desc_object_customization_names": {
        "ja": "選択されたオブジェクトカスタマイズ tag。",
        "ko": "선택된 객체 커스터마이즈 tag.",
    },
    "config.desc_player_traits_1": {
        "ja": "カスタム gameplay 修飾子プリセットスロット 1。",
        "ko": "사용자 지정 gameplay 수정자 프리셋 슬롯 1.",
    },
    "config.desc_player_traits_2": {
        "ja": "カスタム gameplay 修飾子プリセットスロット 2。",
        "ko": "사용자 지정 gameplay 수정자 프리셋 슬롯 2.",
    },
    "config.desc_player_traits_3": {
        "ja": "カスタム gameplay 修飾子プリセットスロット 3。",
        "ko": "사용자 지정 gameplay 수정자 프리셋 슬롯 3.",
    },
    "config.desc_player_traits_4": {
        "ja": "カスタム gameplay 修飾子プリセットスロット 4。",
        "ko": "사용자 지정 gameplay 수정자 프리셋 슬롯 4.",
    },
    "config.desc_pos": {
        "ja": "画面ピクセル単位のウィンドウ位置。",
        "ko": "화면 픽셀 단위의 창 위치.",
    },
    "config.desc_resolution_scale": {
        "ja": "内部レンダリング解像度の倍率。",
        "ko": "내부 렌더링 해상도 배율.",
    },
    "config.desc_resolution_size_x": {
        "ja": "表示幅（ピクセル）。",
        "ko": "표시 너비(픽셀).",
    },
    "config.desc_resolution_size_y": {
        "ja": "表示高さ（ピクセル）。",
        "ko": "표시 높이(픽셀).",
    },
    "config.desc_size": {
        "ja": "ウィンドウの幅と高さ（ピクセル）。",
        "ko": "창 너비 및 높이(픽셀).",
    },
    "config.discard_and_reload": {
        "ja": "破棄して再読み込み",
        "ko": "버리고 다시 로드",
    },
    "config.discard_unsaved_changes_message": {
        "ja": "再読み込みすると、各エディタータブのテキストがディスク上の現在のファイルに置き換わります。",
        "ko": "다시 로드하면 모든 편집기 탭의 텍스트가 디스크의 현재 파일로 교체됩니다.",
    },
    "config.discard_unsaved_changes_title": {
        "ja": "未保存の設定変更を破棄しますか？",
        "ko": "저장되지 않은 구성 변경을 버릴까요?",
    },
    "config.fullscreen": {
        "ja": "全画面",
        "ko": "전체 화면",
    },
    "config.item_n": {
        "ja": "項目 {0}",
        "ko": "항목 {0}",
    },
    "config.quality_high": {
        "ja": "高",
        "ko": "높음",
    },
    "config.quality_low": {
        "ja": "低",
        "ko": "낮음",
    },
    "config.quality_medium": {
        "ja": "中",
        "ko": "중간",
    },
    "config.restore_backup_message": {
        "ja": "{0}\n\n現在のファイルを先にバックアップし、このスナップショットのファイルが一致する設定ファイルを上書きします。",
        "ko": "{0}\n\n현재 파일을 먼저 백업한 다음, 이 스냅샷의 파일이 일치하는 구성 파일을 덮어씁니다.",
    },
    "config.restore_backup_title": {
        "ja": "この設定バックアップを復元しますか？",
        "ko": "이 구성 백업을 복원할까요?",
    },
    "config.restored_config_files": {
        "ja": "{0} 個の設定ファイルを復元しました。以前のディスク上の状態もバックアップされました。",
        "ko": "{0}개 구성 파일을 복원했습니다. 이전 디스크 상태도 백업되었습니다.",
    },
    "config.saved_and_reloaded_halo": {
        "ja": "{0} 個の設定ファイルを保存し、ゲーム内の Halo 設定を再読み込みしました。Gameplay 修飾子の変更は、次回そのプリセットが適用されるか、現在のチェックポイントが再読み込みされたときに有効になります。",
        "ko": "{0}개 구성 파일을 저장하고 게임 내 Halo 설정을 다시 로드했습니다. Gameplay 수정자 변경은 다음에 해당 프리셋이 적용되거나 현재 체크포인트가 다시 로드될 때 적용됩니다.",
    },
    "config.saved_config_files": {
        "ja": "{0} 個の設定ファイルを保存しました。バックアップ: {1}",
        "ko": "{0}개 구성 파일을 저장했습니다. 백업: {1}",
    },
    "config.saved_live_reload_failed": {
        "ja": "ファイルとバックアップは保存されましたが、ライブゲームの再読み込みに失敗しました: {0}",
        "ko": "파일과 백업은 저장되었지만 라이브 게임 다시 로드에 실패했습니다: {0}",
    },
    "config.saved_not_updated": {
        "ja": "{0} 個の設定ファイルを保存しましたが、実行中のゲームは更新されませんでした。{1} バックアップ: {2}",
        "ko": "{0}개 구성 파일을 저장했지만 실행 중인 게임은 업데이트되지 않았습니다. {1} 백업: {2}",
    },
    "config.saved_reload_not_confirmed": {
        "ja": "ファイルは保存されましたが、Campaign Evolved がライブ再読み込みを確認しませんでした: {0}",
        "ko": "파일은 저장되었지만 Campaign Evolved가 라이브 다시 로드를 확인하지 않았습니다: {0}",
    },
    "config.settings_count": {
        "ja": "{0} 件の設定",
        "ko": "{0}개 설정",
    },
    "config.settings_filtered_count": {
        "ja": "{0} / {1}",
        "ko": "{0} / {1}",
    },
    "config.trait_active_camo": {
        "ja": "アクティブカモ",
        "ko": "액티브 카모",
    },
    "config.trait_appearance": {
        "ja": "外見",
        "ko": "외형",
    },
    "config.trait_damage_resistance": {
        "ja": "ダメージ耐性",
        "ko": "피해 저항",
    },
    "config.trait_gravity": {
        "ja": "重力",
        "ko": "중력",
    },
    "config.trait_infinite_ammo": {
        "ja": "無限弾薬",
        "ko": "무한 탄약",
    },
    "config.trait_melee_damage": {
        "ja": "近接ダメージ",
        "ko": "근접 피해",
    },
    "config.trait_movement": {
        "ja": "移動",
        "ko": "이동",
    },
    "config.trait_movement_speed": {
        "ja": "移動速度",
        "ko": "이동 속도",
    },
    "config.trait_shield_recharge_rate": {
        "ja": "シールド充電速度",
        "ko": "실드 충전 속도",
    },
    "config.trait_vitality": {
        "ja": "生命力",
        "ko": "생존",
    },
    "config.trait_weapon_damage": {
        "ja": "武器ダメージ",
        "ko": "무기 피해",
    },
    "config.trait_weapons": {
        "ja": "武器",
        "ko": "무기",
    },
    "config.unassigned_item_n": {
        "ja": "未割り当て項目 {0}",
        "ko": "할당되지 않은 항목 {0}",
    },
    "config.unsaved_discarded_reloaded": {
        "ja": "未保存の変更を破棄し、設定ファイルを再読み込みしました。",
        "ko": "저장되지 않은 변경을 버리고 구성 파일을 다시 로드했습니다.",
    },
    "config.value": {
        "ja": "値",
        "ko": "값",
    },
    "config.windowed": {
        "ja": "ウィンドウ",
        "ko": "창 모드",
    },
    "customization.applied_armor_live": {
        "ja": "{0} をライブプレイヤーに即座に適用しました。",
        "ko": "{0}을(를) 라이브 플레이어에 즉시 적용했습니다.",
    },
    "customization.applied_weapon_live": {
        "ja": "所持している {1} に {0} を即座に適用しました。",
        "ko": "들고 있는 {1}에 {0}을(를) 즉시 적용했습니다.",
    },
    "customization.armor_no_model_variant": {
        "ja": "そのアーマー選択には一致する Master Chief モデルバリアントがありません。",
        "ko": "해당 아머 선택에 일치하는 Master Chief 모델 변형이 없습니다.",
    },
    "customization.cannot_equip_no_entitlement": {
        "ja": "Halo Meister が対応する所有 entitlement を確認できないため、製品版では {0} を装備できません。",
        "ko": "Halo Meister가 해당 소유 entitlement를 확인할 수 없어 리테일 빌드에서는 {0}을(를) 장착할 수 없습니다.",
    },
    "customization.cannot_equip_not_owned": {
        "ja": "読み込まれたアカウントが {1} を所有していないため、{0} を装備できません。",
        "ko": "로드된 계정이 {1}을(를) 소유하지 않아 {0}을(를) 장착할 수 없습니다.",
    },
    "customization.custom_future_item": {
        "ja": "カスタム/将来アイテム ({0})",
        "ko": "사용자 지정/향후 항목 ({0})",
    },
    "customization.download_save_before_equip": {
        "ja": "{0} を装備する前にアカウントの PlayFab セーブをダウンロードしてください。製品版ビルドは {1} を確認する必要があります。",
        "ko": "{0}을(를) 장착하기 전에 계정의 PlayFab 세이브를 다운로드하세요. 리테일 빌드는 {1}을(를) 확인해야 합니다.",
    },
    "customization.loaded_overrides": {
        "ja": "{0} 件の装備上書きを読み込みました",
        "ko": "{0}개 장착 오버라이드를 로드했습니다",
    },
    "customization.loaded_overrides_unrecognized": {
        "ja": "{0} 件の装備上書きを読み込み、{1} 件の認識できない tag を保持しました。",
        "ko": "{0}개 장착 오버라이드를 로드하고 {1}개의 인식되지 않은 tag를 보존했습니다.",
    },
    "customization.ownership_verified_suffix": {
        "ja": "{0} · 所有権確認済み ({1})",
        "ko": "{0} · 소유권 확인됨 ({1})",
    },
    "customization.preserved_from_config": {
        "ja": "現在の設定から保持",
        "ko": "현재 구성에서 보존됨",
    },
    "customization.ready_message": {
        "ja": "ランタイム選択は profile ごとに保存されます。オフライン campaign ミッションを開始し、Customization を開くと自動適用されます。",
        "ko": "런타임 선택은 profile별로 저장됩니다. 오프라인 campaign 미션을 시작하고 Customization을 열면 자동 적용됩니다.",
    },
    "customization.ready_to_edit": {
        "ja": "この profile を編集できます",
        "ko": "이 profile을 편집할 수 있습니다",
    },
    "customization.retail_ownership_protection": {
        "ja": "製品版コスメティック所有権保護",
        "ko": "리테일 코스메틱 소유권 보호",
    },
    "customization.retail_safety_message": {
        "ja": "Mark VI やその他のゲーム既定外見は entitlement なしで利用できます。プロモーションアーマーと武器スキンは、読み込まれたセーブに一致する OwnedPlayFabEntitlements エントリがある場合のみ利用できます。ストアパック、実験的、非表示/ランタイム専用、不明なアイテムは Halo Meister がライセンスを確認できないため利用できません。これにより製品版アプリがコスメティック所有権の回避手段にならないようにし、著作権、ライセンス、DMCA 削除リスクを低減します。現在 {0} 件のカタログ選択が利用できません",
        "ko": "Mark VI 및 기타 게임 기본 외형은 entitlement 없이 사용할 수 있습니다. 프로모션 아머와 무기 스킨은 로드된 세이브에 일치하는 OwnedPlayFabEntitlements 항목이 있을 때만 사용할 수 있습니다. 스토어 팩, 실험적, 숨김/런타임 전용 및 알 수 없는 항목은 Halo Meister가 라이선스를 확인할 수 없어 사용할 수 없습니다. 이는 리테일 앱이 코스메틱 소유권 우회 수단이 되는 것을 방지하고 저작권, 라이선스 및 DMCA 삭제 위험을 줄입니다. 현재 {0}개의 카탈로그 선택을 사용할 수 없습니다",
    },
    "customization.retail_safety_running_suffix": {
        "ja": " ローカル profile 変更を保存する前にゲームを終了してください。",
        "ko": " 로컬 profile 변경을 저장하기 전에 게임을 종료하세요.",
    },
    "customization.retail_safety_unverified_suffix": {
        "ja": "；{0} 件の未確認の設定選択は適用されず、変更を保存するとスロット既定値に置き換えられます。",
        "ko": "; {0}개의 미확인 구성 선택은 적용되지 않으며, 변경을 저장하면 슬롯 기본값으로 대체됩니다.",
    },
    "customization.retail_safety_verify_suffix": {
        "ja": " Progress & profile でアカウントの PlayFab セーブをダウンロードして、プロモーション所有権を確認してください。",
        "ko": " Progress & profile에서 계정 PlayFab 세이브를 다운로드하여 프로모션 소유권을 확인하세요.",
    },
    "customization.running_notice_message": {
        "ja": "アーマーまたは武器の外見を選ぶと即座に適用されます。ランタイム選択は profile ごとに保存され、一致するプレイヤーまたは武器が現れたときに自動的に再試行されます。",
        "ko": "아머 또는 무기 외형을 선택하면 즉시 적용됩니다. 런타임 선택은 profile별로 저장되며, 일치하는 플레이어 또는 무기가 나타날 때 자동으로 재시도됩니다.",
    },
    "customization.running_notice_title": {
        "ja": "Campaign Evolved が実行中です",
        "ko": "Campaign Evolved가 실행 중입니다",
    },
    "customization.saved_for_profile": {
        "ja": "{0} を {1} 用に保存しました。{2} が利用可能になったときに自動適用されます。",
        "ko": "{0}을(를) {1}용으로 저장했습니다. {2}을(를) 사용할 수 있게 되면 자동 적용됩니다.",
    },
    "customization.saved_selections": {
        "ja": "所有コスメティック選択を保存しました。バックアップ: {0}。",
        "ko": "소유 코스메틱 선택을 저장했습니다. 백업: {0}.",
    },
    "customization.unsaved_changes": {
        "ja": "未保存の変更",
        "ko": "저장되지 않은 변경",
    },
    "customization.unsaved_close_game": {
        "ja": "未保存の変更 — 保存するにはゲームを終了して再読み込みしてください",
        "ko": "저장되지 않은 변경 — 저장하려면 게임을 종료하고 다시 로드하세요",
    },
    "customization.weapon_no_model_variant": {
        "ja": "その {0} 選択には一致するライブモデルバリアントがありません。",
        "ko": "해당 {0} 선택에 일치하는 라이브 모델 변형이 없습니다.",
    },
    "player_tools.authored_camera_active": {
        "ja": "{0} の作成済みユニットカメラがプレイヤーで有効です。",
        "ko": "{0}의 제작된 유닛 카메라가 플레이어에서 활성화되었습니다.",
    },
    "player_tools.authored_camera_restored": {
        "ja": "プレイヤーの作成済みユニットカメラを復元しました。",
        "ko": "플레이어의 제작된 유닛 카메라를 복원했습니다.",
    },
    "player_tools.authored_timing_restored": {
        "ja": "作成済み武器アニメーション中断タイミングを復元しました。",
        "ko": "제작된 무기 애니메이션 중단 타이밍을 복원했습니다.",
    },
    "player_tools.current_position": {
        "ja": "現在位置: {0}",
        "ko": "현재 위치: {0}",
    },
    "player_tools.custom_camera_active": {
        "ja": "カスタムプレイヤー付着カメラが有効です。",
        "ko": "사용자 지정 플레이어 부착 카메라가 활성화되었습니다.",
    },
    "player_tools.deleted_location": {
        "ja": "保存位置「{0}」を削除しました。",
        "ko": "저장 위치 \"{0}\"을(를) 삭제했습니다.",
    },
    "player_tools.enter_finite_camera_values": {
        "ja": "有限のローカル X、Y、Z 値と 30 から 150 度の FOV を入力してください。",
        "ko": "유한한 로컬 X, Y, Z 값과 30~150도 FOV를 입력하세요.",
    },
    "player_tools.enter_finite_xyz": {
        "ja": "X、Y、Z に有限の数値を入力してください。小数点にはピリオドを使用してください。",
        "ko": "X, Y, Z에 유한한 숫자를 입력하세요. 소수점 구분자로 마침표를 사용하세요.",
    },
    "player_tools.found_camera_presets": {
        "ja": "{1} 用の読み込み済みユニットカメラプリセットを {0} 件見つけました。",
        "ko": "{1}에 대해 로드된 유닛 카메라 프리셋 {0}개를 찾았습니다.",
    },
    "player_tools.immediate_interruption_active": {
        "ja": "即時武器アニメーション中断が有効です。近接と武器切り替えを今すぐ比較してください。",
        "ko": "즉시 무기 애니메이션 중단이 활성화되었습니다. 지금 근접과 무기 전환을 비교해 보세요.",
    },
    "player_tools.moved_interruption_markers": {
        "ja": "{1} 個の一人称アニメーション graph で {0} 個の中断マーカーをフレーム 0 に移動しました。",
        "ko": "{1}개의 1인칭 애니메이션 graph에서 {0}개의 중단 마커를 프레임 0으로 이동했습니다.",
    },
    "player_tools.no_camera_preset": {
        "ja": "カメラプリセットが選択されていません。",
        "ko": "카메라 프리셋이 선택되지 않았습니다.",
    },
    "player_tools.no_camera_restore_needed": {
        "ja": "復元が必要なアクティブカメラ tag 値はありませんでした。",
        "ko": "복원이 필요한 활성 카메라 tag 값이 없습니다.",
    },
    "player_tools.bridge_overlook": {
        "ja": "ブリッジ見晴らし",
        "ko": "브리지 전망",
    },
    "player_tools.no_clip": {
        "ja": "No-clip",
        "ko": "노클립",
    },
    "player_tools.no_clip_enabled": {
        "ja": "No-clip が有効です。保存またはミッションを離れる前に通常の物理を復元してください。",
        "ko": "No-clip이 활성화되었습니다. 저장하거나 미션을 떠나기 전에 일반 물리를 복원하세요.",
    },
    "player_tools.no_clip_off": {
        "ja": "No-clip はオフです。",
        "ko": "No-clip이 꺼져 있습니다.",
    },
    "player_tools.no_clip_on_summary": {
        "ja": "No-clip がオンです。Campaign 飛行はカメラと制御 biped を一緒に保ちます。",
        "ko": "No-clip이 켜져 있습니다. Campaign 비행은 카메라와 제어 biped를 함께 유지합니다.",
    },
    "player_tools.no_saved_location": {
        "ja": "保存位置が選択されていません。",
        "ko": "저장된 위치가 선택되지 않았습니다.",
    },
    "player_tools.normal_input_restored": {
        "ja": "通常のプレイヤー入力を復元しました。",
        "ko": "일반 플레이어 입력을 복원했습니다.",
    },
    "player_tools.normal_physics_restored": {
        "ja": "通常のプレイヤー物理と衝突を復元しました。",
        "ko": "일반 플레이어 물리 및 충돌을 복원했습니다.",
    },
    "player_tools.normal_punch_restored": {
        "ja": "通常の近接加速度を復元しました。",
        "ko": "일반 근접 가속도를 복원했습니다.",
    },
    "player_tools.player_input_restored": {
        "ja": "プレイヤー入力を復元しました。",
        "ko": "플레이어 입력을 복원했습니다.",
    },
    "player_tools.player_input_suppressed": {
        "ja": "プレイヤー入力が抑制されています。Unreal カメラセッション終了時にプレイヤー入力を復元してください。",
        "ko": "플레이어 입력이 억제되었습니다. Unreal 카메라 세션이 끝나면 플레이어 입력을 복원하세요.",
    },
    "player_tools.player_input_suppressed_summary": {
        "ja": "プレイヤーシミュレーション入力が抑制されています。Unreal カメラレイヤーは引き続き利用できます。",
        "ko": "플레이어 시뮬레이션 입력이 억제되었습니다. Unreal 카메라 레이어는 계속 사용할 수 있습니다.",
    },
    "player_tools.read_position": {
        "ja": "制御プレイヤーのライブ位置を読み取りました。",
        "ko": "제어 플레이어의 라이브 위치를 읽었습니다.",
    },
    "player_tools.ready_refresh": {
        "ja": "準備完了。位置を更新するか、上でワークスペースを選んでください。",
        "ko": "준비 완료. 위치를 새로 고치거나 위에서 작업 공간을 선택하세요.",
    },
    "player_tools.refresh_cameras_first": {
        "ja": "先にユニットカメラプリセットを更新して選択してください。",
        "ko": "먼저 유닛 카메라 프리셋을 새로 고치고 선택하세요.",
    },
    "player_tools.refresh_cameras_status": {
        "ja": "作成済み biped と車両カメラを更新しました。カスタム値はプレイヤーの現在の track を表示します。",
        "ko": "제작된 biped 및 차량 카메라를 새로 고쳤습니다. 사용자 지정 값은 플레이어의 현재 track을 표시합니다.",
    },
    "player_tools.refresh_unit_cameras": {
        "ja": "先にユニットカメラを更新してください。",
        "ko": "먼저 유닛 카메라를 새로 고치세요.",
    },
    "player_tools.restored_camera_values": {
        "ja": "{0} 個の作成済みカメラ値を復元しました。",
        "ko": "{0}개의 제작된 카메라 값을 복원했습니다.",
    },
    "player_tools.restored_interruption_markers": {
        "ja": "{0} 個の武器アニメーション中断マーカーを復元しました。",
        "ko": "{0}개의 무기 애니메이션 중단 마커를 복원했습니다.",
    },
    "player_tools.restored_melee_effects": {
        "ja": "{0} 個の近接効果を通常のノックバックに復元しました。",
        "ko": "{0}개의 근접 효과를 일반 넉백으로 복원했습니다.",
    },
    "player_tools.returned_position": {
        "ja": "保存位置に戻りました。",
        "ko": "저장된 위치로 돌아갔습니다.",
    },
    "player_tools.returned_to": {
        "ja": "{0} に戻りました",
        "ko": "{0}(으)로 돌아갔습니다",
    },
    "player_tools.saved_at": {
        "ja": "「{0}」を {1} に保存しました。",
        "ko": "\"{0}\"을(를) {1}에 저장했습니다.",
    },
    "player_tools.saved_return_position": {
        "ja": "復帰位置を保存しました: {0}",
        "ko": "복귀 위치 저장: {0}",
    },
    "player_tools.saved_session_position": {
        "ja": "現在位置をこのアプリセッション用に保存しました。",
        "ko": "현재 위치를 이 앱 세션용으로 저장했습니다.",
    },
    "player_tools.simulation_confirmed_teleport": {
        "ja": "ネイティブシミュレーションが新しいプレイヤー位置を確認しました。",
        "ko": "네이티브 시뮬레이션이 새 플레이어 위치를 확인했습니다.",
    },
    "player_tools.super_punch": {
        "ja": "Super Punch",
        "ko": "Super Punch",
    },
    "player_tools.super_punch_active": {
        "ja": "Super Punch が {0}x で有効です。{1} 個の読み込み済み近接効果に適用されています。",
        "ko": "Super Punch가 {0}x로 활성화되었습니다. {1}개의 로드된 근접 효과에 적용됩니다.",
    },
    "player_tools.super_punch_enabled": {
        "ja": "Super Punch を {0}x で有効にしました。移動可能なオブジェクトを打って飛ばし方を確認してください。",
        "ko": "Super Punch가 {0}x로 활성화되었습니다. 이동 가능한 물체를 쳐서 날아가는 것을 확인하세요.",
    },
    "player_tools.super_punch_no_restore": {
        "ja": "Super Punch に復元するライブ値がありませんでした。",
        "ko": "Super Punch에 복원할 라이브 값이 없었습니다.",
    },
    "player_tools.teleport_confirmed": {
        "ja": "{0} にテレポートしました",
        "ko": "{0}(으)로 텔레포트했습니다",
    },
    "player_tools.teleport_saved": {
        "ja": "保存位置「{0}」にテレポートしました。",
        "ko": "저장 위치 \"{0}\"(으)로 텔레포트했습니다.",
    },
    "player_tools.teleport_saved_detail": {
        "ja": "「{0}」にテレポートしました: {1}",
        "ko": "\"{0}\"(으)로 텔레포트: {1}",
    },
    "runtime_tags.acknowledge_spawn_warning": {
        "ja": "生成する前に実験的ネイティブ呼び出し警告を確認してください。",
        "ko": "생성하기 전에 실험적 네이티브 호출 경고를 확인하세요.",
    },
    "runtime_tags.applied_tag_config": {
        "ja": "「{0}」を適用しました: {2} 個の tag に {1} 件のフィールド変更を書き込みました。{3}",
        "ko": "'{0}' 적용: {2}개 tag에 {1}개 필드 변경을 기록했습니다.{3}",
    },
    "runtime_tags.apply_configuration": {
        "ja": "設定を適用",
        "ko": "구성 적용",
    },
    "runtime_tags.apply_tag_config_title": {
        "ja": "tag 設定「{0}」を適用しますか？",
        "ko": "tag 구성 '{0}'을(를) 적용할까요?",
    },
    "runtime_tags.branches_collapsed": {
        "ja": "{0} 件一致 · 速度のため分岐を折りたたみ",
        "ko": "{0}개 일치 · 속도를 위해 분기 접음",
    },
    "runtime_tags.build_native_mod_count": {
        "ja": "ネイティブ mod を構築 ({0})",
        "ko": "네이티브 mod 빌드 ({0})",
    },
    "runtime_tags.built_native_mod": {
        "ja": "{1} 個の tag の {0} 件のフィールド変更からネイティブ mod をネイティブ overlay として構築しました:\n{2}\n{3}\n{4}\n\n編集可能プロジェクト: {5}",
        "ko": "{1}개 tag의 {0}개 필드 변경으로 네이티브 mod를 네이티브 overlay로 빌드했습니다:\n{2}\n{3}\n{4}\n\n편집 가능 프로젝트: {5}",
    },
    "runtime_tags.changed_reference": {
        "ja": "{0} を {1} [{2}] に変更し、ランタイム参照を確認しました。",
        "ko": "{0}을(를) {1} [{2}](으)로 변경하고 런타임 참조를 확인했습니다.",
    },
    "runtime_tags.choose_element_range": {
        "ja": "0 から {0} までの要素を選んでください。",
        "ko": "0부터 {0}까지의 요소를 선택하세요.",
    },
    "runtime_tags.clear_draft_confirm": {
        "ja": "これにより {0} 件の追跡済みフィールド変更が破棄され、新しい設定を開始できます。すでにライブメモリに書き込まれた値は元に戻りません。",
        "ko": "이렇게 하면 {0}개의 추적된 필드 변경이 삭제되어 새 구성을 시작할 수 있습니다. 이미 라이브 메모리에 기록된 값은 되돌리지 않습니다.",
    },
    "runtime_tags.clear_draft_title": {
        "ja": "設定ドラフトを消去しますか？",
        "ko": "구성 초안을 지울까요?",
    },
    "runtime_tags.cleared_draft": {
        "ja": "設定ドラフトを消去しました。ライブ tag 値は変更されていません。",
        "ko": "구성 초안을 지웠습니다. 라이브 tag 값은 변경되지 않았습니다.",
    },
    "runtime_tags.compatibility_disabled": {
        "ja": "互換性チェックが無効です",
        "ko": "호환성 검사 비활성화됨",
    },
    "runtime_tags.compatibility_disabled_message": {
        "ja": "選択した tag のグループは、このフィールドで許可されるグループと異なる場合があります。ゲームが参照を拒否したり、予測不能な動作をしたり、クラッシュする可能性があります。",
        "ko": "선택한 tag의 그룹이 이 필드에서 허용되는 그룹과 다를 수 있습니다. 게임이 참조를 거부하거나 예측 불가능하게 동작하거나 충돌할 수 있습니다.",
    },
    "runtime_tags.could_not_index_nested": {
        "ja": "ネストされたフィールドをインデックスできませんでした: {0}",
        "ko": "중첩 필드를 인덱싱할 수 없습니다: {0}",
    },
    "runtime_tags.enter_raw_byte_count": {
        "ja": "生バイト数を入力してください。",
        "ko": "원시 바이트 수를 입력하세요.",
    },
    "runtime_tags.experimental_reference_swap": {
        "ja": "実験的参照スワップ: {0}",
        "ko": "실험적 참조 교체: {0}",
    },
    "runtime_tags.experimental_spawn_explanation": {
        "ja": "実験的生成は、有効なシミュレーションコンテキストを持つスレッドでネイティブ Blam 配置イニシャライザーとオブジェクトアロケーターをキューに入れます。置換は引き続き利用できません。",
        "ko": "실험적 생성은 유효한 시뮬레이션 컨텍스트를 가진 스레드에서 네이티브 Blam 배치 초기화기와 객체 할당기를 대기열에 넣습니다. 교체는 여전히 사용할 수 없습니다.",
    },
    "runtime_tags.experimental_spawn_label": {
        "ja": "実験的生成",
        "ko": "실험적 생성",
    },
    "runtime_tags.experimentally_changed_reference": {
        "ja": "tag グループ互換性を確認せずに {0} を {1} [{2}] に実験的に変更しました。ランタイム書き込みは確認されました。",
        "ko": "tag 그룹 호환성을 확인하지 않고 {0}을(를) {1} [{2}](으)로 실험적으로 변경했습니다. 런타임 쓰기가 확인되었습니다.",
    },
    "runtime_tags.field_search_results": {
        "ja": "検索結果",
        "ko": "검색 결과",
    },
    "runtime_tags.folder_tag_count": {
        "ja": "{0} 個の tag",
        "ko": "{0}개 tag",
    },
    "runtime_tags.index_capped": {
        "ja": "{0} 件一致 · インデックス上限",
        "ko": "{0}개 일치 · 인덱스 상한",
    },
    "runtime_tags.indexed_count": {
        "ja": "{0} 件インデックス済み",
        "ko": "{0}개 인덱싱됨",
    },
    "runtime_tags.indexing_nested": {
        "ja": "ネスト block をインデックス中…",
        "ko": "중첩 block 인덱싱 중…",
    },
    "runtime_tags.inject_raw_confirm": {
        "ja": "0x{0:X} のライブメモリを上書きします。このページを更新または離れた後、書き込みは自動的に元に戻せません。",
        "ko": "0x{0:X}의 라이브 메모리를 덮어씁니다. 이 페이지를 새로 고치거나 떠난 후에는 쓰기를 자동으로 되돌릴 수 없습니다.",
    },
    "runtime_tags.inject_raw_title": {
        "ja": "{0} バイトの生データを注入しますか？",
        "ko": "{0}개의 원시 바이트를 주입할까요?",
    },
    "runtime_tags.injected_bytes": {
        "ja": "{1} に {0} バイトを注入し、読み戻しを確認しました。",
        "ko": "{1}에 {0}바이트를 주입하고 읽기 확인을 검증했습니다.",
    },
    "runtime_tags.injected_raw_bytes": {
        "ja": "{0} バイトの生データを注入して確認しました。",
        "ko": "{0}개의 원시 바이트를 주입하고 확인했습니다.",
    },
    "runtime_tags.install_native_mod_confirm": {
        "ja": "Halo Meister はこの .utoc と一致する .ucas/.pak ファイルを Meteorite/Content/Paks にコピーします。ゲームは終了している必要があります。既存ファイルは上書きされません。3 つの overlay ファイルを削除すると mod をアンインストールできます。",
        "ko": "Halo Meister가 이 .utoc와 일치하는 .ucas/.pak 파일을 Meteorite/Content/Paks에 복사합니다. 게임을 종료해야 합니다. 기존 파일은 덮어쓰지 않습니다. 세 overlay 파일을 삭제하면 mod를 제거할 수 있습니다.",
    },
    "runtime_tags.install_native_mod_title": {
        "ja": "ネイティブ mod「{0}」をインストールしますか？",
        "ko": "네이티브 mod '{0}'을(를) 설치할까요?",
    },
    "runtime_tags.installed_overlay": {
        "ja": "「{0}」を {1} にインストールしました。overlay を読み込むにはゲームを起動してください。",
        "ko": "'{0}'을(를) {1}에 설치했습니다. overlay를 로드하려면 게임을 시작하세요.",
    },
    "runtime_tags.live_tag_error": {
        "ja": "リアルタイム tag エラー",
        "ko": "실시간 tag 오류",
    },
    "runtime_tags.match_count": {
        "ja": "{0} 件一致",
        "ko": "{0}개 일치",
    },
    "runtime_tags.matches_showing_limit": {
        "ja": "{0} 件一致 · 最初の {1} 件を表示 · フィルターを絞り込んでください",
        "ko": "{0}개 일치 · 처음 {1}개 표시 · 필터를 세분화하세요",
    },
    "runtime_tags.missing_tags_suffix": {
        "ja": " {0} 個の tag がこのミッションで読み込まれていません: {1}。",
        "ko": " {0}개 tag가 이 미션에서 로드되지 않았습니다: {1}.",
    },
    "runtime_tags.nested_index_unavailable": {
        "ja": "ネストインデックス利用不可",
        "ko": "중첩 인덱스 사용 불가",
    },
    "runtime_tags.no_resolvable_root": {
        "ja": "この tag には解決可能なルートデータ block がありません。",
        "ko": "이 tag에는 해석 가능한 루트 데이터 block이 없습니다.",
    },
    "runtime_tags.no_schema_fields": {
        "ja": "[{0}] schema は読み込まれましたが、ルート構造からフィールドが生成されませんでした。",
        "ko": "[{0}] schema가 로드되었지만 루트 구조에서 필드가 생성되지 않았습니다.",
    },
    "runtime_tags.no_schema_for_group": {
        "ja": "[{0}] 用の Baboon schema が読み込まれていません。生バイト検査は引き続き利用できます。",
        "ko": "[{0}]에 대한 Baboon schema가 로드되지 않았습니다. 원시 바이트 검사는 여전히 사용할 수 있습니다.",
    },
    "runtime_tags.placeholder_search_tags": {
        "ja": "パスまたはグループを入力してください。例: elite_ai または [hlmt]",
        "ko": "경로 또는 그룹을 입력하세요. 예: elite_ai 또는 [hlmt]",
    },
    "runtime_tags.raw_injection_exact_length": {
        "ja": "生注入は正確に {0} バイトである必要があります。解析結果は {1} です。",
        "ko": "원시 주입은 정확히 {0}바이트여야 합니다. 파싱 결과는 {1}입니다.",
    },
    "runtime_tags.refreshed_tags": {
        "ja": "{0} 個のライブ tag を更新しました。",
        "ko": "{0}개 라이브 tag를 새로 고쳤습니다.",
    },
    "runtime_tags.save_configuration_count": {
        "ja": "設定を保存 ({0})",
        "ko": "구성 저장 ({0})",
    },
    "runtime_tags.saved_tag_config": {
        "ja": "{1} 個の tag にわたる {0} 件のポータブルフィールド変更を {2} に保存しました。生バイト編集は含まれません。",
        "ko": "{1}개 tag에 걸친 {0}개의 이식 가능한 필드 변경을 {2}에 저장했습니다. 원시 바이트 편집은 포함되지 않습니다.",
    },
    "runtime_tags.scanned_connection": {
        "ja": "{0} 個のライブ tag をスキャン · {1} 個の Baboon schema を読み込み。",
        "ko": "{0}개 라이브 tag 스캔 · {1}개 Baboon schema 로드됨.",
    },
    "runtime_tags.search_all_loaded_tags": {
        "ja": "読み込み済み tag をすべて検索",
        "ko": "로드된 모든 tag 검색",
    },
    "runtime_tags.swap_without_check": {
        "ja": "互換性チェックなしでスワップ",
        "ko": "호환성 검사 없이 교체",
    },
    "runtime_tags.tag_config_contains": {
        "ja": "この設定には {1} 個の tag にわたる {0} 件のフィールド変更が含まれます。Halo Meister は現在のミッションに対して各 tag、ネスト block、tag 参照を解決し、ライブ値を書き込んで確認します。",
        "ko": "이 구성에는 {1}개 tag에 걸친 {0}개의 필드 변경이 포함됩니다. Halo Meister는 현재 미션에 대해 각 tag, 중첩 block 및 tag 참조를 해석한 다음 라이브 값을 기록하고 확인합니다.",
    },
    "runtime_tags.tags_folder_cached": {
        "ja": "{0} 個の tag · フォルダーツリーをキャッシュ済み",
        "ko": "{0}개 tag · 폴더 트리 캐시됨",
    },
    "runtime_tags.waiting_for_typing": {
        "ja": "入力待ち…",
        "ko": "입력 대기 중…",
    },
    "scripting.autocomplete_common": {
        "ja": "よく使うコマンド · Tab または Enter で補完 · Esc で閉じる",
        "ko": "자주 쓰는 명령 · Tab 또는 Enter로 완성 · Esc로 닫기",
    },
    "scripting.autocomplete_prefix": {
        "ja": "「{0}」を補完 · Tab または Enter で確定 · Esc で閉じる",
        "ko": "「{0}」 완성 · Tab 또는 Enter로 수락 · Esc로 닫기",
    },
    "scripting.autocomplete_source": {
        "ja": "オートコンプリート · {0}",
        "ko": "자동 완성 · {0}",
    },
    "scripting.catalog_count": {
        "ja": "{0} 件のシグネチャ",
        "ko": "{0}개 시그니처",
    },
    "scripting.catalog_source": {
        "ja": "カタログ · {0}",
        "ko": "카탈로그 · {0}",
    },
    "scripting.catalog_unavailable": {
        "ja": "カタログ利用不可",
        "ko": "카탈로그 사용 불가",
    },
    "scripting.characters_kib": {
        "ja": "{0} 文字  •  {1} / 64 KiB",
        "ko": "{0}자  •  {1} / 64 KiB",
    },
    "scripting.example_active_player": {
        "ja": "アクティブプレイヤー",
        "ko": "활성 플레이어",
    },
    "scripting.example_hello_lua": {
        "ja": "Lua からの挨拶",
        "ko": "Lua 인사",
    },
    "scripting.example_hide_hud": {
        "ja": "HUD を非表示",
        "ko": "HUD 숨기기",
    },
    "scripting.example_instant_fade_in": {
        "ja": "即時フェードイン",
        "ko": "즉시 페이드 인",
    },
    "scripting.example_instant_fade_out": {
        "ja": "即時フェードアウト",
        "ko": "즉시 페이드 아웃",
    },
    "scripting.example_kill_player": {
        "ja": "プレイヤー 0 を倒す",
        "ko": "플레이어 0 처치",
    },
    "scripting.example_player_position": {
        "ja": "プレイヤー位置",
        "ko": "플레이어 위치",
    },
    "scripting.example_show_hud": {
        "ja": "HUD を表示",
        "ko": "HUD 표시",
    },
    "scripting.file_type_lua": {
        "ja": "Lua スクリプト",
        "ko": "Lua 스크립트",
    },
    "scripting.file_type_text": {
        "ja": "テキストファイル",
        "ko": "텍스트 파일",
    },
    "scripting.haloscript_submitted": {
        "ja": "Campaign Evolved に送信しました。アクティブミッションで効果を確認してください。",
        "ko": "Campaign Evolved에 제출했습니다. 활성 미션에서 효과를 확인하세요.",
    },
    "scripting.language_error": {
        "ja": "{0} · エラー",
        "ko": "{0} · 오류",
    },
    "scripting.mailbox_path": {
        "ja": "メールボックス: {0}",
        "ko": "메일박스: {0}",
    },
    "scripting.outcome_confirmed": {
        "ja": "確認済み",
        "ko": "확인됨",
    },
    "scripting.outcome_error": {
        "ja": "エラー",
        "ko": "오류",
    },
    "scripting.outcome_submitted": {
        "ja": "送信済み（未確認）",
        "ko": "제출됨(미확인)",
    },
    "scripting.ready_bridge": {
        "ja": "準備完了 · bridge v{0} · {1}",
        "ko": "준비 완료 · bridge v{0} · {1}",
    },
    "scripting.request_cancelled": {
        "ja": "リクエスト待機をキャンセルしました。",
        "ko": "요청 대기가 취소되었습니다.",
    },
    "scripting.request_submitted": {
        "ja": "> {0} リクエストを送信しました",
        "ko": "> {0} 요청 제출됨",
    },
    "scripting.run_haloscript_button": {
        "ja": "HaloScript を実行",
        "ko": "HaloScript 실행",
    },
    "scripting.run_lua_button": {
        "ja": "Lua を実行",
        "ko": "Lua 실행",
    },
    "scripting.runtime_lua": {
        "ja": "ゲームスレッド上で実行され、UE4SS グローバルと Unreal オブジェクト API が利用できます。",
        "ko": "게임 스레드에서 실행되며 UE4SS 전역 변수와 Unreal 객체 API를 사용할 수 있습니다.",
    },
    "scripting.saved_file": {
        "ja": "{0} を保存しました。",
        "ko": "{0}을(를) 저장했습니다.",
    },
    "scripting.scripting_title": {
        "ja": "スクリプト",
        "ko": "스크립팅",
    },
    "scripting.starter_source": {
        "ja": "スターター · {0}",
        "ko": "시작 · {0}",
    },
    "scripting.submitted_not_verified": {
        "ja": "送信済み、未確認",
        "ko": "제출됨, 미확인",
    },
    "spawner.all_categories": {
        "ja": "すべてのカテゴリ",
        "ko": "모든 범주",
    },
    "spawner.armor_spawn_success": {
        "ja": "Johnson の AI が操縦する Spartan {0} を作成しました。{1}",
        "ko": "Johnson AI가 조종하는 Spartan {0}을(를) 생성했습니다. {1}",
    },
    "spawner.batch_failed": {
        "ja": "バッチが失敗する前に {0} 個のキュー済み actor を作成しました。{1}",
        "ko": "배치 실패 전에 {0}개의 대기 actor를 생성했습니다. {1}",
    },
    "spawner.catalog_armor_subtitle": {
        "ja": "Johnson の戦闘 AI、選択可能な Spartan アーマー",
        "ko": "Johnson 전투 AI, 선택 가능한 Spartan 아머",
    },
    "spawner.catalog_characters_subtitle": {
        "ja": "一意の読み込み済みキャラクター tag",
        "ko": "고유 로드된 캐릭터 tag",
    },
    "spawner.catalog_no_armor": {
        "ja": "認識可能なアーマーモデルが解決されませんでした",
        "ko": "인식 가능한 아머 모델을 해석하지 못했습니다",
    },
    "spawner.catalog_vehicles_subtitle": {
        "ja": "読み込み済み車両 tag",
        "ko": "로드된 차량 tag",
    },
    "spawner.character_ai_detail": {
        "ja": "{0} キャラクター AI",
        "ko": "{0} 캐릭터 AI",
    },
    "spawner.character_spawn_success": {
        "ja": "{1} を使用して {0} を作成しました。{2}",
        "ko": "{1}을(를) 사용해 {0}을(를) 생성했습니다. {2}",
    },
    "spawner.created_armor": {
        "ja": "{0} を作成しました。{1}",
        "ko": "{0}을(를) 생성했습니다. {1}",
    },
    "spawner.created_mixed_team": {
        "ja": "{0} 体の AI の完全混合チームを作成しました。",
        "ko": "{0}개 AI의 완전 혼합 팀을 생성했습니다.",
    },
    "spawner.default_weapon": {
        "ja": "既定武器",
        "ko": "기본 무기",
    },
    "spawner.empty_no_loaded": {
        "ja": "現在のミッションではこのタイプの生成可能オブジェクトが読み込まれていません。",
        "ko": "현재 미션에서 이 유형의 생성 가능 객체가 로드되지 않았습니다.",
    },
    "spawner.empty_no_match": {
        "ja": "現在の検索とカテゴリに一致するオブジェクトがありません。",
        "ko": "현재 검색 및 범주와 일치하는 객체가 없습니다.",
    },
    "spawner.filter_all": {
        "ja": "すべて",
        "ko": "전체",
    },
    "spawner.fixed_variant": {
        "ja": "固定バリアント",
        "ko": "고정 변형",
    },
    "spawner.friendly_companion_label": {
        "ja": "友好コンパニオン",
        "ko": "우호 동료",
    },
    "spawner.friendly_companion_requires_v86": {
        "ja": "友好コンパニオン生成には bridge v86 が必要です。新しくインストールした bridge を読み込むため、一度ゲームを再起動してください。",
        "ko": "우호 동료 생성에는 bridge v86이 필요합니다. 새로 설치된 bridge를 로드하려면 게임을 한 번 재시작하세요.",
    },
    "spawner.hostile": {
        "ja": "敵対",
        "ko": "적대",
    },
    "spawner.loaded_catalog": {
        "ja": "一意のキャラクター {0} 体、Spartan アーマーバリアント {1} 件、車両 {2} 台を読み込みました。",
        "ko": "고유 캐릭터 {0}개, Spartan 아머 변형 {1}개, 차량 {2}대를 로드했습니다.",
    },
    "spawner.mixed_team_support": {
        "ja": "混合チームは現在、キャラクター AI と Spartan アーマー AI をサポートしています。",
        "ko": "혼합 팀은 현재 캐릭터 AI와 Spartan 아머 AI를 지원합니다.",
    },
    "spawner.no_team_selections": {
        "ja": "混合チームに少なくとも 1 つの選択を追加してください。",
        "ko": "혼합 팀에 최소 하나의 선택을 추가하세요.",
    },
    "spawner.queued_armor_unavailable": {
        "ja": "キュー済み Spartan アーマーは利用できません。",
        "ko": "대기 중인 Spartan 아머를 사용할 수 없습니다.",
    },
    "spawner.random_mission_weapons": {
        "ja": "ランダムミッション武器",
        "ko": "무작위 미션 무기",
    },
    "spawner.random_variants": {
        "ja": "ランダムバリアント",
        "ko": "무작위 변형",
    },
    "spawner.scan_armor_default": {
        "ja": "読み込み済みミッションをスキャンして Spartan アーマーを解決してください。",
        "ko": "로드된 미션을 스캔하여 Spartan 아머를 해석하세요.",
    },
    "spawner.select_armor": {
        "ja": "先に Spartan アーマーエントリを選択してください。",
        "ko": "먼저 Spartan 아머 항목을 선택하세요.",
    },
    "spawner.select_armor_variant": {
        "ja": "先にアーマーバリアントを選択してください。",
        "ko": "먼저 아머 변형을 선택하세요.",
    },
    "spawner.select_character": {
        "ja": "先にキャラクターを選択してください。",
        "ko": "먼저 캐릭터를 선택하세요.",
    },
    "spawner.select_character_variant": {
        "ja": "先にキャラクターバリアントを選択してください。",
        "ko": "먼저 캐릭터 변형을 선택하세요.",
    },
    "spawner.select_vehicle": {
        "ja": "先に車両を選択してください。",
        "ko": "먼저 차량을 선택하세요.",
    },
    "spawner.spawn_team_success": {
        "ja": "{1} を使用して 5 人の {0} チームを送信しました。{2}",
        "ko": "{1}을(를) 사용해 5인 {0} 팀을 제출했습니다. {2}",
    },
}

PLACEHOLDER_PATTERN = re.compile(r"\{(\d+)(?::[^}]*)?\}")


def extract_placeholders(text: str) -> list[str]:
    return PLACEHOLDER_PATTERN.findall(text)


def validate_placeholders(key: str, en_text: str, translated: str, lang: str) -> None:
    en_ph = extract_placeholders(en_text)
    tr_ph = extract_placeholders(translated)
    if sorted(en_ph) != sorted(tr_ph):
        raise ValueError(
            f"{key} [{lang}]: placeholder mismatch en={en_ph} {lang}={tr_ph}\n"
            f"  en: {en_text!r}\n  {lang}: {translated!r}"
        )


def load_json(path: Path) -> dict[str, str]:
    return json.loads(path.read_text(encoding="utf-8"))


def write_json(path: Path, data: dict[str, str]) -> None:
    sorted_data = dict(sorted(data.items()))
    path.write_text(
        json.dumps(sorted_data, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )


def keys_needing_translation(en: dict[str, str], zh: dict[str, str], target: dict[str, str]) -> list[str]:
    return sorted(
        k
        for k in en
        if zh.get(k) != en.get(k) and target.get(k) == en.get(k)
    )


def main() -> None:
    en_path = I18N_DIR / "en.json"
    zh_path = I18N_DIR / "zh-Hans.json"
    ja_path = I18N_DIR / "ja.json"
    ko_path = I18N_DIR / "ko.json"

    en = load_json(en_path)
    zh = load_json(zh_path)
    ja = load_json(ja_path)
    ko = load_json(ko_path)

    ja_needs_before = keys_needing_translation(en, zh, ja)
    ko_needs_before = keys_needing_translation(en, zh, ko)

    missing_in_dict = [k for k in ja_needs_before if k not in TRANSLATIONS]
    if missing_in_dict:
        raise SystemExit(
            f"TRANSLATIONS missing {len(missing_in_dict)} keys: {missing_in_dict[:10]}..."
        )

    extra_in_dict = sorted(set(TRANSLATIONS) - set(ja_needs_before))
    if extra_in_dict:
        print(f"Warning: {len(extra_in_dict)} keys in TRANSLATIONS not currently needed")

    ja_updated = 0
    ko_updated = 0

    for key in ja_needs_before:
        entry = TRANSLATIONS[key]
        en_text = en[key]

        ja_text = entry["ja"]
        ko_text = entry["ko"]

        validate_placeholders(key, en_text, ja_text, "ja")
        validate_placeholders(key, en_text, ko_text, "ko")

        if ja.get(key) == en.get(key):
            ja[key] = ja_text
            ja_updated += 1
        if ko.get(key) == en.get(key):
            ko[key] = ko_text
            ko_updated += 1

    write_json(ja_path, ja)
    write_json(ko_path, ko)

    ja_after = keys_needing_translation(en, zh, ja)
    ko_after = keys_needing_translation(en, zh, ko)

    print(f"Keys needing ja before: {len(ja_needs_before)}")
    print(f"Keys needing ko before: {len(ko_needs_before)}")
    print(f"ja keys updated: {ja_updated}")
    print(f"ko keys updated: {ko_updated}")
    print(f"Keys needing ja after: {len(ja_after)}")
    print(f"Keys needing ko after: {len(ko_after)}")

    if ja_after:
        print("Remaining ja keys (may be legitimately identical):")
        for k in ja_after:
            print(f"  {k}: en={en[k]!r} ja={ja[k]!r}")
    if ko_after:
        print("Remaining ko keys (may be legitimately identical):")
        for k in ko_after:
            print(f"  {k}: en={en[k]!r} ko={ko[k]!r}")

    if ja_after or ko_after:
        allowed_identical = {
            "player_tools.no_clip",
            "player_tools.super_punch",
        }
        unexpected_ja = [k for k in ja_after if k not in allowed_identical]
        unexpected_ko = [k for k in ko_after if k not in allowed_identical]
        if unexpected_ja or unexpected_ko:
            raise SystemExit(1)


if __name__ == "__main__":
    main()
