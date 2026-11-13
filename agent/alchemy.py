"""Alchemy click guards and per-order retry accounting; navigation stays in Pipeline."""

import re
import time
from dataclasses import dataclass, field

from bootstrap import configure_runtime

configure_runtime()

from maa.agent.agent_server import AgentServer
from maa.custom_action import CustomAction
from maa.custom_recognition import CustomRecognition
from maa.pipeline import JActionType, JClick

from common import safe_json_loads


@dataclass
class AlchemySession:
    attempts: dict[tuple[int, int], int] = field(default_factory=dict)
    total: int | None = None
    remaining: int | None = None
    navigation_clicked_at: dict[str, float] = field(default_factory=dict)


_SESSIONS: dict[str, AlchemySession] = {}
ATTEMPT_LIMIT = 3


def _session(context):
    return _SESSIONS[context.tasker.controller.uuid]


def _hit(context, image, node):
    result = context.run_recognition(node, image)
    return result if result is not None and result.hit else None


def _delivery(context, image):
    session = _session(context)
    if not _hit(context, image, "__DailyAlchemyOrdersPageReady"):
        return None, {"reason": "not_alchemy_overview"}
    count = _hit(context, image, "__DailyAlchemyOrdersCount")
    text = " ".join(getattr(item, "text", "") for item in (count.all_results or [])) if count else ""
    match = re.fullmatch(r"订单\s*([0-9])\s*/\s*([79])", text.strip())
    if not match:
        return None, {"reason": "order_count_unconfirmed", "text": text}
    remaining, total = map(int, match.groups())
    detail = {"remaining": remaining, "total": total, "limit": ATTEMPT_LIMIT}
    if remaining <= 0 or remaining > total:
        return None, {**detail, "reason": "no_orders_or_invalid_count"}
    if session.total is not None and (total != session.total or remaining > session.remaining):
        return None, {**detail, "reason": "order_count_changed_unexpectedly"}
    used = session.attempts.get((total, remaining), 0)
    if used >= ATTEMPT_LIMIT:
        return None, {**detail, "reason": "current_order_attempt_limit", "used": used}
    marker = _hit(context, image, "__DailyAlchemyOrdersDeliverable")
    if marker is None or marker.box is None:
        return None, {**detail, "reason": "no_deliverable_marker"}
    x, y, width, height = map(int, marker.box)
    if width <= 0 or height <= 0:
        return None, {**detail, "reason": "invalid_marker_box"}
    point = (x + width // 2 + 67, y + height // 2 + 76)
    return (point[0] - 8, point[1] - 8, 16, 16), {**detail, "attempt": used + 1, "point": point}


@AgentServer.custom_action("DailyAlchemyInitialize")
class DailyAlchemyInitialize(CustomAction):
    def run(self, context, argv):
        try:
            _SESSIONS[context.tasker.controller.uuid] = AlchemySession()
            return True
        except Exception as error:
            print(f"[炼金] 初始化失败：{error}", flush=True)
            return False


@AgentServer.custom_recognition("DailyAlchemyOrderTarget")
class DailyAlchemyOrderTarget(CustomRecognition):
    def analyze(self, context, argv):
        try:
            box, detail = _delivery(context, argv.image)
            return CustomRecognition.AnalyzeResult(box=box, detail=detail)
        except Exception as error:
            return CustomRecognition.AnalyzeResult(box=None, detail={"reason": "order_recognition_error", "error": str(error)})


@AgentServer.custom_action("DailyAlchemyClickCurrent")
class DailyAlchemyClickCurrent(CustomAction):
    def run(self, context, argv):
        try:
            raw = argv.custom_action_param
            param = safe_json_loads(raw, {}) if isinstance(raw, str) else raw
            target = param.get("target") if isinstance(param, dict) else None
            if target not in ("DailyAlchemyOrdersOpenAlchemy", "DailyAlchemyOrdersOpenDrawer", "delivery"):
                return False
            # Reuse the active controller, including its configured capture method.
            # The page may have changed during the native pre-click stability wait.
            capture = context.tasker.controller.post_screencap().wait()
            if not capture.succeeded:
                return False
            image = context.tasker.controller.cached_image
            if target == "delivery":
                box, detail = _delivery(context, image)
            else:
                result = _hit(context, image, target)
                box, detail = (result.box if result else None), {}
            if box is None:
                print("[炼金] 点击前画面或订单状态已变化，本次不点击，交回页面识别。", flush=True)
                return True
            x, y, width, height = map(int, box)
            if width <= 0 or height <= 0:
                return False
            if target == "delivery":
                session = _session(context)
                previous = session.remaining
                remaining, total = detail["remaining"], detail["total"]
                # Reserve before issuing input; uncertain responses cannot replenish retries.
                session.attempts[(total, remaining)] = detail["attempt"]
                session.total, session.remaining = total, remaining
                if previous is not None and remaining < previous:
                    print(f"[炼金] 已确认订单减少：{previous} → {remaining}。", flush=True)
                print(f"[炼金] 剩余订单{remaining}/{total}，点击订单中心，本订单第{detail['attempt']}/3次尝试。", flush=True)
            else:
                session = _session(context)
                now = time.monotonic()
                if now - session.navigation_clicked_at.get(target, float("-inf")) < 3.0:
                    # Entry animations can retain the old button briefly. Give the
                    # previous input time to switch pages, then recognize again.
                    return True
                session.navigation_clicked_at[target] = now
            result = context.run_action_direct(JActionType.Click, JClick(target=(x + width // 2, y + height // 2)))
            return result is not None and result.success
        except Exception as error:
            print(f"[炼金] 点击前复核失败：{error}", flush=True)
            return False
