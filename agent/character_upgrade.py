"""Locate upgrade controls and keep a bounded ledger without driving page sequences."""

import re
import time
from dataclasses import dataclass

from bootstrap import configure_runtime

configure_runtime()

from maa.agent.agent_server import AgentServer
from maa.custom_action import CustomAction
from maa.custom_recognition import CustomRecognition
from maa.pipeline import JOCR

from common import safe_json_loads


PREFIX = "__DailyCharacterUpgrade"
PAGE_BOX = (0, 0, 720, 1280)


@dataclass
class UpgradeSession:
    issued: int = 0
    confirmed: int = 0
    pending: dict | None = None
    filter_reset: bool = False
    filtered: bool = False
    card_selected: bool = False
    panel_cleared: bool = False
    handed_off: bool = False
    stopped: str = ""
    last_selection: float = 0


_SESSIONS: dict[str, UpgradeSession] = {}


def _session(context):
    # The outer reward entry initializes once. RunTask clones and game retries
    # retain the same controller UUID, so neither can replenish this ledger.
    return _SESSIONS[context.tasker.controller.uuid]


def _mode(raw):
    param = safe_json_loads(raw, {}) if isinstance(raw, str) else raw
    return param.get("mode") if isinstance(param, dict) else None


def _find(context, image, name, roi=None):
    node = PREFIX + name
    override = {node: {"recognition": {"param": {"roi": list(roi)}}}} if roi else None
    result = context.run_recognition(node, image, override)
    return result if result is not None and result.hit and result.box is not None else None


def _ocr(context, image, roi, only_rec=True):
    result = context.run_recognition_direct(
        "OCR", JOCR(expected=[], roi=tuple(roi), only_rec=only_rec, threshold=0.3), image
    )
    if result is None:
        raise RuntimeError("角色强化OCR未能启动")
    return " ".join(item.text for item in result.all_results if getattr(item, "text", ""))


def _integer(text):
    value = text.strip().replace("，", ",")
    if not re.fullmatch(r"[0-9]+|[0-9]{1,3}(?:,[0-9]{3})+", value):
        return None
    return int(value.replace(",", ""))


def _panel(context, image):
    return _find(context, image, "StrengthenTab") and _find(context, image, "LowestPotion")


def _overview(context, image):
    return _find(context, image, "OverviewInfo") and _find(context, image, "OverviewUpgrade")


def _snapshot(context, image):
    if not _panel(context, image):
        return None
    count_text = _ocr(context, image, (101, 856, 111, 30))
    title = re.sub(r"\W+", "", _ocr(context, image, (14, 122, 560, 58), False))
    preview_text = _ocr(context, image, (265, 906, 220, 32)).replace(" ", "")
    preview = re.search(r"[+＋](\d+)[eE][xX][pP]", preview_text)
    selected = bool(_find(context, image, "SelectedOne", (166, 785, 37, 43)))
    other_selected = any(
        _find(context, image, "SelectedOne", (x, 785, 42, 43)) for x in (305, 449, 590)
    )
    has_preview = bool(preview or "+" in preview_text or "＋" in preview_text or "exp" in preview_text.lower())
    return {
        "stock": _integer(count_text),
        "stock_text": count_text,
        "title": title,
        "experience": int(preview[1]) if preview else None,
        "preview_text": preview_text,
        "selected_one": selected,
        "other_selected": other_selected,
        "empty_selection": not has_preview and not selected and not other_selected,
    }


def _finished(session):
    return session.confirmed >= 2 or bool(session.stopped)


def _locate(context, image, session, mode):
    detail = {"mode": mode, "issued": session.issued, "confirmed": session.confirmed, "reason": session.stopped}
    if mode == "needed":
        return (PAGE_BOX if not session.handed_off else None), detail
    if mode == "pending_expired":
        expired = session.pending and time.monotonic() - session.pending["time"] >= 20
        return (PAGE_BOX if expired else None), detail
    if mode == "pending_wait":
        return (PAGE_BOX if session.pending else None), detail
    if mode == "settled":
        if session.pending:
            current = _snapshot(context, image)
            before = session.pending["before"]
            detail["snapshot"] = current
            if (
                current and current["empty_selection"] and current["title"] == before["title"]
                and current["stock"] == before["stock"] - 1
            ):
                return PAGE_BOX, detail
        return None, detail
    if mode == "handoff":
        result = context.run_recognition("GameMainReady", image) if _finished(session) else None
        return (result.box if result and result.hit else None), detail
    if mode == "return_home":
        result = _find(context, image, "Castle") if _finished(session) else None
        return (result.box if result else None), detail
    if mode == "close_filter":
        result = _find(context, image, "FilterClose") if _finished(session) and _find(context, image, "FilterTitle") else None
        return (result.box if result else None), detail
    if mode == "return_back":
        result = context.run_recognition("TkfmRecoveryBackButtonTarget", image) if _finished(session) else None
        return (result.box if result and result.hit else None), detail
    if session.pending or _finished(session):
        return None, detail

    if mode in {"reset_filter", "confirm_filter", "cycle_filter", "scroll_filter"}:
        if not _find(context, image, "FilterTitle"):
            return None, detail
        if mode == "reset_filter":
            button = _find(context, image, "FilterReset") if not session.filter_reset else None
            return (button.box if button else None), detail
        if not session.filter_reset:
            return None, detail
        label = _find(context, image, "LevelLabel")
        if mode == "scroll_filter":
            return (PAGE_BOX if not label else None), detail
        if not label:
            return None, detail
        _, y, _, height = (int(value) for value in label.box)
        row = (452, max(130, y - 17), 152, min(height + 34, 80))
        not_max = _find(context, image, "LevelNotMax", row)
        if mode == "confirm_filter":
            button = _find(context, image, "FilterConfirm") if not_max else None
            return (button.box if button else None), detail
        if not not_max and (_find(context, image, "LevelAll", row) or _find(context, image, "LevelMax", row)):
            return row, detail
        return None, detail

    if mode in {"open_filter", "select_card", "open_upgrade"}:
        if not _overview(context, image):
            return None, detail
        if mode == "open_filter" and not session.filtered:
            star = _find(context, image, "FavoriteStar")
            if star:
                x, y, width, height = (int(value) for value in star.box)
                return (x + width + 17, y - 1, 110, height + 2), detail
        if mode == "select_card" and session.filtered and not session.card_selected:
            cards = context.run_recognition(PREFIX + "CardLevels", image)
            candidates = []
            for item in cards.filtered_results if cards and cards.hit else []:
                text = getattr(item, "text", "")
                if not re.search(r"[lL][vV]\s*\d{1,3}", text):
                    continue
                x, y, width, height = (int(value) for value in item.box)
                if 190 <= x <= 540 and 870 <= y <= 925:
                    candidates.append((x, y, height, text))
            if candidates:
                x, y, height, text = min(candidates)
                detail["card_level_text"] = text
                return (x - 5, y + height - 322, 110, 322), detail
        if mode == "open_upgrade" and session.card_selected:
            button = _find(context, image, "OverviewUpgrade")
            return (button.box if button else None), detail
        return None, detail

    if mode == "open_characters":
        ready = context.run_recognition("GameMainReady", image)
        button = _find(context, image, "CharacterTab") if ready and ready.hit else None
        return (button.box if button else None), detail

    if mode == "restart_home":
        if _panel(context, image) or _overview(context, image) or _find(context, image, "FilterTitle"):
            return None, detail
        button = _find(context, image, "Castle")
        return (button.box if button else None), detail

    if mode in {"clear_selection", "select_potion", "submit", "no_stock", "leave_unselected_panel"}:
        current = _snapshot(context, image)
        detail["snapshot"] = current
        if not current:
            return None, detail
        if mode == "leave_unselected_panel":
            button = _find(context, image, "Castle") if not session.card_selected else None
            return (button.box if button else None), detail
        if not session.card_selected:
            return None, detail
        if mode == "no_stock":
            return (PAGE_BOX if current["stock"] == 0 else None), detail
        approved = (
            current["selected_one"] and not current["other_selected"]
            and current["experience"] == 200 and current["stock"] is not None
            and current["stock"] > 0 and bool(current["title"])
        )
        if mode == "clear_selection":
            clear = not session.panel_cleared or (not current["empty_selection"] and not approved)
            button = _find(context, image, "Clear") if clear else None
            return (button.box if button else None), detail
        if mode == "select_potion":
            allowed = (
                session.panel_cleared and current["empty_selection"] and bool(current["title"])
                and current["stock"] is not None and current["stock"] > 0
                and time.monotonic() - session.last_selection >= 2
            )
            button = _find(context, image, "LowestPotion") if allowed else None
            return (button.box if button else None), detail
        if mode == "submit" and approved and session.issued < 2:
            button = _find(context, image, "Submit")
            return (button.box if button else None), detail
        return None, detail
    raise ValueError("未知角色强化识别模式：" + str(mode))


@AgentServer.custom_recognition("DailyCharacterUpgradeState")
class DailyCharacterUpgradeState(CustomRecognition):
    def analyze(self, context, argv):
        try:
            box, detail = _locate(context, argv.image, _session(context), _mode(argv.custom_recognition_param))
            return CustomRecognition.AnalyzeResult(box=box, detail=detail)
        except Exception as error:
            return CustomRecognition.AnalyzeResult(box=None, detail={"reason": "upgrade_recognition_error", "error": str(error)})


@AgentServer.custom_action("DailyCharacterUpgradeInitialize")
class DailyCharacterUpgradeInitialize(CustomAction):
    def run(self, context, argv):
        try:
            _SESSIONS[context.tasker.controller.uuid] = UpgradeSession()
            print("[领奖前强化] 本轮最多强化两次，每次只用最左侧最低级药水1瓶；完成后继续原领奖流程。", flush=True)
            return True
        except Exception as error:
            print(f"[领奖前强化] 初始化失败：{error}", flush=True)
            return False


@AgentServer.custom_action("DailyCharacterUpgradeAction")
class DailyCharacterUpgradeAction(CustomAction):
    def run(self, context, argv):
        try:
            session = _session(context)
            mode = _mode(argv.custom_action_param)
            if mode in {"skip", "pending_expired"}:
                session.stopped = ("强化提交响应未能确认，保留已提交额度并停止补点" if session.pending
                                   else "未找到可用的未满级角色、最低级药水或可靠页面")
                session.pending = None
                print(f"[领奖前强化] {session.stopped}；已确认{session.confirmed}/2次，继续原领奖。", flush=True)
                return True
            image = context.tasker.controller.cached_image
            if image is None:
                return False
            box, detail = _locate(context, image, session, mode)
            if box is None:
                return False
            if mode == "settled":
                session.confirmed += 1
                session.pending = None
                session.panel_cleared = True
                print(f"[领奖前强化] 已确认第{session.confirmed}/2次：最低级药水减少1瓶，选择已清空。", flush=True)
                return True
            if mode == "handoff":
                session.handed_off = True
                print(f"[领奖前强化] 已回主页，确认强化{session.confirmed}/2次，开始原有奖励领取。", flush=True)
                return True
            if mode == "no_stock":
                session.stopped = "最左侧最低级药水已用尽，不替换高级药水"
                print(f"[领奖前强化] {session.stopped}，继续原领奖。", flush=True)
                return True
            if mode == "submit":
                # Reserve before input. An uncertain response is never retried as a new
                # transaction, including after a game restart or navigation recovery.
                session.issued += 1
                session.pending = {"before": detail["snapshot"], "time": time.monotonic()}
                print(f"[领奖前强化] 第{session.issued}/2次提交：{detail['snapshot']['title']}，最低级药水1瓶，+200exp；等待实际扣除。", flush=True)
            if mode == "select_potion":
                session.last_selection = time.monotonic()
            click = context.run_action("DailyCharacterUpgradeClickCenter", box=box)
            if click is None or not click.success:
                return False
            if mode == "reset_filter":
                session.filter_reset = True
            elif mode == "confirm_filter":
                session.filtered = True
            elif mode == "select_card":
                session.card_selected = True
                print(f"[领奖前强化] 已定位完整角色卡：{detail['card_level_text']}，准备进入升级。", flush=True)
            elif mode == "clear_selection":
                session.panel_cleared = True
            elif mode == "open_characters":
                # A game restart or external navigation can discard the filter.
                # Rebuild navigation permission while keeping consumption limits.
                session.filter_reset = False
                session.filtered = False
                session.card_selected = False
                session.panel_cleared = False
            return True
        except Exception as error:
            print(f"[领奖前强化] 动作失败：{error}", flush=True)
            return False
