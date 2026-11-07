import itertools
import re
import time

from bootstrap import configure_runtime

configure_runtime()

from maa.agent.agent_server import AgentServer
from maa.custom_action import CustomAction
from maa.custom_recognition import CustomRecognition
from maa.pipeline import JOCR

from common import safe_json_loads


TAG_ROI = (95, 440, 530, 165)
REFRESH_ROI = (370, 610, 230, 70)
TAG_SLOT_ROIS = [
    (116, 463, 149, 51),
    (283, 462, 156, 54),
    (453, 466, 153, 47),
    (113, 533, 160, 54),
    (284, 538, 156, 53),
]
MAX_SELECTABLE_TAGS = 3
READ_CONFIRM_INTERVAL = 0.8
OVERVIEW_CONFIRM_INTERVAL = 0.5
RECRUITMENT_SLOTS = (1, 2, 3, 4)
OVERVIEW_BOX = (80, 90, 160, 80)
SETTLED_STATES = frozenset({"ready", "skipped_leader"})
EMPTY_STATES = frozenset({"empty", "skipped_leader"})
KNOWN_OVERVIEW_STATES = frozenset({"empty", "skipped_leader", "ready", "recruiting"})
BATCH_MODES = frozenset(
    {"slot_settled", "batch_ready", "all_skipped", "collected", "entry_collected", "cycle_active"}
)
DECISION_MODES = frozenset(
    {
        "ready", "skip_leader", "refresh", "recruit_guaranteed", "recruit_fallback",
        "select_tag", "tag_selected", "selection_complete", "prepare", "submit",
    }
)
_SESSION_TASK_ID = None
_SLOT_SESSIONS = {}
_OVERVIEW_OBSERVATIONS = {}
RECRUITMENT_PENDING_ANCHOR = "TkfmRecruitmentPending"
RECRUITMENT_BUDGET_MODES = frozenset({"can_open", "started", "leader_skipped", "can_accelerate", "finished"})

RECRUITMENT_POOL = [
    {"name": "魔王 巴尔", "stars": 3, "tags": {"领袖", "火属性", "魔族", "中体型", "攻击者", "爆发力", "输出"}},
    {"name": "魔王 撒旦", "stars": 3, "tags": {"领袖", "闇属性", "魔族", "中体型", "守护者", "生存力", "回击", "巨乳", "防御", "保护"}},
    {"name": "精灵王 赛露西亚", "stars": 3, "tags": {"领袖", "风属性", "亚人", "中体型", "巨乳", "辅助者", "爆发力", "支援"}},
    {"name": "矮人王 兰儿", "stars": 3, "tags": {"领袖", "水属性", "亚人", "小体型", "贫乳", "攻击者", "爆发力", "越战越强", "输出"}},
    {"name": "法斯公主 露露", "stars": 3, "tags": {"领袖", "风属性", "人类", "治疗者", "回复"}},
    {"name": "魔王 伊布力斯", "stars": 3, "tags": {"领袖", "光属性", "魔族", "中体型", "攻击者", "生存力", "输出", "群体攻击"}},
    {"name": "古代勇者 乌鲁塔", "stars": 3, "tags": {"领袖", "风属性", "守护者", "人类", "中体型", "保护", "防御"}},
    {"name": "现代勇者 神田绫音", "stars": 3, "tags": {"领袖", "光属性", "攻击者", "人类", "爆发力", "输出", "中体型"}},
    {"name": "贤者 白", "stars": 3, "tags": {"领袖", "风属性", "治疗者", "亚人", "中体型", "支援", "回复"}},
    {"name": "毒蝎 莫默", "stars": 3, "tags": {"领袖", "水属性", "攻击者", "魔族", "中体型", "贫乳", "爆发力", "输出"}},
    {"name": "魔管家 艾可", "stars": 2, "tags": {"菁英", "闇属性", "魔族", "辅助者", "中体型", "美乳", "支援"}},
    {"name": "圣骑士长 雷欧娜", "stars": 2, "tags": {"菁英", "水属性", "人类", "守护者", "美乳", "中体型", "生存力", "保护", "防御"}},
    {"name": "神官长 菲欧菈", "stars": 2, "tags": {"菁英", "光属性", "人类", "治疗者", "美乳", "中体型", "回复"}},
    {"name": "女忍者 凛月", "stars": 2, "tags": {"菁英", "风属性", "人类", "攻击者", "美乳", "中体型", "输出", "群体攻击", "爆发力"}},
    {"name": "剑圣 神无雪", "stars": 2, "tags": {"菁英", "火属性", "亚人", "攻击者", "美乳", "中体型", "越战越强", "削弱"}},
    {"name": "妖狐 静", "stars": 2, "tags": {"菁英", "水属性", "美乳", "妨碍者", "亚人", "小体型", "削弱", "干扰"}},
    {"name": "大将军 朱诺安", "stars": 2, "tags": {"菁英", "闇属性", "人类", "攻击者", "巨乳", "中体型", "输出", "支援"}},
    {"name": "天才女军师 布兰妮", "stars": 2, "tags": {"菁英", "光属性", "人类", "妨碍者", "美乳", "中体型", "群体攻击", "削弱", "爆发力", "支援"}},
    {"name": "史莱姆女王 娜芙拉拉", "stars": 2, "tags": {"菁英", "风属性", "魔族", "守护者", "巨乳", "中体型", "保护", "防御", "生存力", "回复"}},
    {"name": "魔法少女 托特拉", "stars": 2, "tags": {"菁英", "光属性", "攻击者", "中体型", "人类", "爆发力", "美乳", "削弱", "输出"}},
    {"name": "最后的银龙 普利特拉", "stars": 2, "tags": {"菁英", "闇属性", "妨碍者", "亚人", "中体型", "削弱", "美乳"}},
    {"name": "刺针 嘉维尔", "stars": 2, "tags": {"菁英", "风属性", "攻击者", "美乳", "人类", "中体型", "输出"}},
    {"name": "精灵舞者 塔诺西雅", "stars": 2, "tags": {"菁英", "光属性", "辅助者", "亚人", "回复", "美乳", "中体型"}},
    {"name": "工会看板娘 小萤", "stars": 2, "tags": {"菁英", "水属性", "治疗者", "支援", "人类", "贫乳", "回复", "中体型"}},
    {"name": "流浪魔法师 尤依", "stars": 1, "tags": {"火属性", "攻击者", "人类", "巨乳", "小体型", "越战越强", "输出"}},
    {"name": "龙女 伊维斯", "stars": 1, "tags": {"火属性", "攻击者", "亚人", "贫乳", "小体型", "越战越强", "群体攻击", "输出"}},
    {"name": "猫妖 娜娜", "stars": 1, "tags": {"风属性", "攻击者", "魔族", "贫乳", "小体型", "输出"}},
    {"name": "美人鱼 玛莲", "stars": 1, "tags": {"水属性", "治疗者", "亚人", "美乳", "中体型", "回复"}},
    {"name": "犬人族 朵拉", "stars": 1, "tags": {"风属性", "守护者", "亚人", "美乳", "保护", "生存力", "中体型", "防御"}},
    {"name": "双蛇军团护士长 艾琳", "stars": 1, "tags": {"光属性", "治疗者", "人类", "巨乳", "中体型", "回复"}},
    {"name": "魅魔 撒芭丝", "stars": 1, "tags": {"闇属性", "妨碍者", "魔族", "美乳", "干扰", "削弱", "中体型"}},
    {"name": "闇黑精灵 索拉卡", "stars": 1, "tags": {"闇属性", "妨碍者", "亚人", "中体型", "削弱", "美乳"}},
    {"name": "白蔷薇 伊艾", "stars": 1, "tags": {"光属性", "治疗者", "人类", "贫乳", "小体型", "回复"}},
    {"name": "法斯帝国法师 佩托拉", "stars": 0, "tags": {"光属性", "攻击者", "人类", "贫乳", "中体型", "群体攻击", "输出", "士兵"}},
    {"name": "矮人战士 可儿", "stars": 0, "tags": {"水属性", "攻击者", "亚人", "贫乳", "小体型", "输出", "爆发力", "士兵"}},
    {"name": "精灵射手 奥菈", "stars": 0, "tags": {"风属性", "攻击者", "亚人", "美乳", "中体型", "输出", "士兵"}},
    {"name": "魔族法师 玛努艾拉", "stars": 0, "tags": {"闇属性", "攻击者", "中体型", "魔族", "美乳", "输出", "士兵"}},
    {"name": "烈日国武士 桔梗", "stars": 0, "tags": {"火属性", "妨碍者", "人类", "美乳", "中体型", "削弱", "士兵"}},
    {"name": "蛇女 拉米亚", "stars": 0, "tags": {"火属性", "妨碍者", "美乳", "中体型", "削弱", "干扰", "士兵"}},
    {"name": "史莱姆娘 萝尔", "stars": 0, "tags": {"水属性", "妨碍者", "魔族", "美乳", "小体型", "削弱", "生存力", "士兵", "回复"}},
    {"name": "鸟身女妖 哈比", "stars": 0, "tags": {"风属性", "妨碍者", "魔族", "美乳", "中体型", "干扰", "士兵"}},
    {"name": "法斯帝国士兵 赛莲", "stars": 0, "tags": {"闇属性", "守护者", "人类", "美乳", "中体型", "保护", "防御", "士兵"}},
    {"name": "牛女 米诺", "stars": 0, "tags": {"风属性", "守护者", "亚人", "巨乳", "中体型", "干扰", "保护", "防御", "生存力", "士兵"}},
    {"name": "魔族战士 芙蕾", "stars": 0, "tags": {"光属性", "守护者", "魔族", "美乳", "中体型", "保护", "士兵", "防御"}},
    {"name": "圣光骑士 玛蒂娜", "stars": 0, "tags": {"光属性", "守护者", "人类", "美乳", "中体型", "保护", "生存力", "防御", "士兵"}},
    {"name": "双蛇军团士兵 夏琳", "stars": 0, "tags": {"火属性", "守护者", "人类", "美乳", "中体型", "防御", "群体攻击", "保护", "士兵"}},
    {"name": "烈日国巫女 枫", "stars": 0, "tags": {"风属性", "治疗者", "人类", "美乳", "中体型", "回复", "士兵"}},
    {"name": "主神教团僧兵 克蕾雅", "stars": 0, "tags": {"光属性", "治疗者", "人类", "美乳", "中体型", "回复", "士兵"}},
    {"name": "试做机三号", "stars": 0, "tags": {"光属性", "攻击者", "小体型", "士兵", "美乳", "输出", "生存力"}},
    {"name": "法斯菁英近卫 安娜", "stars": 0, "tags": {"火属性", "守护者", "人类", "中体型", "防御", "士兵", "保护", "美乳"}},
    {"name": "法斯高阶法师 诺诺可", "stars": 0, "tags": {"水属性", "攻击者", "人类", "中体型", "美乳", "输出", "士兵", "爆发力"}},
    {"name": "法斯精锐骑士 布兰", "stars": 0, "tags": {"风属性", "攻击者", "人类", "中体型", "美乳", "输出", "防御", "士兵"}},
    {"name": "惩戒天使", "stars": 0, "tags": {"水属性", "守护者", "士兵", "生存力", "中体型", "回击", "群体攻击", "美乳", "亚人"}},
    {"name": "福音天使", "stars": 0, "tags": {"水属性", "治疗者", "士兵", "保护", "美乳", "中体型"}},
    {"name": "木乃伊 穆穆", "stars": 0, "tags": {"闇属性", "生存力", "妨碍者", "中体型", "保护", "干扰", "美乳"}},
    {"name": "人马 赛希", "stars": 0, "tags": {"风属性", "巨乳", "中体型", "攻击者", "亚人", "爆发力", "输出"}},
    {"name": "猎犬小队 茉莉", "stars": 0, "tags": {"水属性", "人类", "贫乳", "攻击者", "小体型", "士兵"}},
    {"name": "猎犬小队 安雅", "stars": 0, "tags": {"风属性", "妨碍者", "士兵", "人类"}},
]

KNOWN_TAGS = sorted({tag for item in RECRUITMENT_POOL for tag in item["tags"]}, key=len, reverse=True)

_CYCLE_HIT_COUNT_NODES = (
    *(
        f"DailyRecruitmentSlot{slot}{suffix}"
        for slot in RECRUITMENT_SLOTS
        for suffix in (
            "SkipLeader", "Open", "Refresh", "RecruitGuaranteed", "RecruitFallback",
            "SelectTag1", "SelectTag2", "SelectTag3",
        )
    ),
    *(
        f"DailyRecruitmentImmediateSlot{slot}{suffix}"
        for slot in RECRUITMENT_SLOTS
        for suffix in ("RecruitButton", "Confirm")
    ),
    "DailyRecruitmentImmediateRecruitButton",
    "DailyRecruitmentImmediateCollectResults",
    "DailyRecruitmentImmediateCollectSkip",
    "DailyRecruitmentImmediateCollectClose",
    "DailyRecruitmentCollectResults",
    "DailyRecruitmentCollectSkip",
    "DailyRecruitmentCollectClose",
)


def _recognition_params(raw) -> dict | None:
    if isinstance(raw, dict):
        return raw
    if isinstance(raw, str):
        parsed = safe_json_loads(raw, None)
        if isinstance(parsed, dict):
            return parsed
    return None


def _valid_slot(slot) -> bool:
    return type(slot) is int and slot in RECRUITMENT_SLOTS


def _recruitment_budget(context):
    config = (context.get_node_data("DailyRecruitmentBudgetConfig") or {}).get("attach", {})
    enabled = True
    maximum = config.get("total", 1)
    if type(maximum) is not int or not 1 <= maximum <= 100:
        raise ValueError("征才总次数必须为 1–100 的整数")
    entries = []
    if enabled:
        for index in range(1, 101):
            entry = context.get_anchor(f"TkfmRecruitmentAttempt{index}")
            if not entry:
                break
            entries.append(entry)
    return enabled, maximum, entries


def _slot_outcome(context, slot):
    return context.get_anchor(f"TkfmRecruitmentSlot{slot}Outcome")


def _planned_acceleration(context, slot):
    return context.get_anchor(f"TkfmRecruitmentSlot{slot}Accelerate") == f"__DailyRecruitmentSlot{slot}CanAccelerate"


def _pending_slot(context):
    pending = context.get_anchor(RECRUITMENT_PENDING_ANCHOR)
    for slot in RECRUITMENT_SLOTS:
        if pending in {
            f"DailyRecruitmentSlot{slot}StartRecruit",
            f"DailyRecruitmentSlot{slot}StartRecruitFallback",
            f"DailyRecruitmentSlot{slot}SkipLeader",
        }:
            return slot
    return None


def _budget_slot_settled(context, slot, state):
    if state in SETTLED_STATES:
        return True
    return state in EMPTY_STATES or (state == "recruiting" and not _planned_acceleration(context, slot))


@AgentServer.custom_recognition("DailyRecruitmentBudgetState")
class DailyRecruitmentBudgetState(CustomRecognition):
    def analyze(self, context, argv):
        try:
            param = _recognition_params(argv.custom_recognition_param)
            if not param or param.get("mode") not in RECRUITMENT_BUDGET_MODES:
                return CustomRecognition.AnalyzeResult(box=None, detail={"reason": "invalid_budget_mode"})
            mode, slot = param["mode"], param.get("slot")
            _ensure_task_session(argv.task_detail.task_id)
            if mode != "finished" and not _valid_slot(slot):
                return CustomRecognition.AnalyzeResult(box=None, detail={"reason": "invalid_slot"})
            enabled, maximum, entries = _recruitment_budget(context)
            detail = {"mode": mode, "slot": slot, "processed": len(entries), "maximum": maximum}
            box = None
            if mode == "can_open":
                allowed = not enabled or (
                    len(entries) < maximum and _pending_slot(context) is None
                    and _slot_outcome(context, slot) not in {
                        f"DailyRecruitmentSlot{slot}CountRecruit", f"DailyRecruitmentSlot{slot}CountLeader"
                    }
                )
                if allowed:
                    result = context.run_recognition(f"__DailyRecruitmentSlot{slot}Empty", argv.image)
                    if result and result.hit:
                        box = result.box
            elif mode == "can_accelerate":
                if len(entries) < maximum and _planned_acceleration(context, slot):
                    box = OVERVIEW_BOX
            elif _overview_is_clear(context, argv.image):
                if mode == "finished":
                    states = [_overview_slot_state(context, argv.image, number) for number in RECRUITMENT_SLOTS]
                    finished = (
                        (len(entries) >= maximum or (maximum <= 4 and "empty" not in states))
                        and _pending_slot(context) is None
                        and "ready" not in states
                        and all(state in KNOWN_OVERVIEW_STATES for state in states)
                    )
                    detail["slots"] = states
                    if finished and _overview_confirmed("budget_finished", states):
                        box = OVERVIEW_BOX
                    elif not finished:
                        _OVERVIEW_OBSERVATIONS.pop("budget_finished", None)
                else:
                    pending = context.get_anchor(RECRUITMENT_PENDING_ANCHOR)
                    expected = (
                        {f"DailyRecruitmentSlot{slot}StartRecruit", f"DailyRecruitmentSlot{slot}StartRecruitFallback"}
                        if mode == "started" else {f"DailyRecruitmentSlot{slot}SkipLeader"}
                    )
                    state = _overview_slot_state(context, argv.image, slot)
                    detail["state"] = state
                    if pending in expected and (
                        state in {"recruiting", "ready"} if mode == "started" else state in EMPTY_STATES
                    ):
                        box = OVERVIEW_BOX
            elif mode == "finished":
                _OVERVIEW_OBSERVATIONS.pop("budget_finished", None)
            return CustomRecognition.AnalyzeResult(box=box, detail=detail)
        except Exception as error:
            _OVERVIEW_OBSERVATIONS.pop("budget_finished", None)
            return CustomRecognition.AnalyzeResult(
                box=None, detail={"reason": "budget_state_failed", "error": str(error)}
            )


@AgentServer.custom_action("DailyRecruitmentRecordAttempt")
class DailyRecruitmentRecordAttempt(CustomAction):
    def run(self, context, argv):
        try:
            param = _recognition_params(argv.custom_action_param)
            if not param or not _valid_slot(param.get("slot")) or param.get("outcome") not in {"Recruit", "Leader"}:
                return False
            slot, outcome = param["slot"], param["outcome"]
            enabled, maximum, entries = _recruitment_budget(context)
            node = f"DailyRecruitmentSlot{slot}Count{outcome}"
            if enabled and _slot_outcome(context, slot) != node:
                # Each ordinal has its own pending marker; the same slot can be recruited in
                # several batches, so the node name alone cannot identify a ledger write.
                appended = bool(entries) and entries[-1] == node and context.get_anchor(
                    f"TkfmRecruitmentWriting{len(entries)}"
                ) == node
                if not appended:
                    if len(entries) >= maximum:
                        return False
                    if not context.set_anchor(f"TkfmRecruitmentWriting{len(entries) + 1}", node):
                        return False
                    if not context.set_anchor(f"TkfmRecruitmentAttempt{len(entries) + 1}", node):
                        return False
                if not context.set_anchor(f"TkfmRecruitmentSlot{slot}Outcome", node):
                    return False
                _, _, entries = _recruitment_budget(context)
                suffix = "；总次数已达标，保留剩余招募自然倒计时" if len(entries) == maximum else ""
                print(f"[全境征才] 已处理 {len(entries)}/{maximum}：栏位{slot}，{'开始征才' if outcome == 'Recruit' else '领袖跳过'}{suffix}", flush=True)
            if not context.set_anchor(f"TkfmRecruitmentWriting{len(entries)}", "DailyRecruitmentBudgetConfig"):
                return False
            return context.set_anchor(RECRUITMENT_PENDING_ANCHOR, "DailyRecruitmentBudgetConfig")
        except Exception as error:
            print(f"[全境征才] 次数记账失败：{error}", flush=True)
            return False


@AgentServer.custom_action("DailyRecruitmentFinishBatch")
class DailyRecruitmentFinishBatch(CustomAction):
    def run(self, context, argv):
        try:
            if _pending_slot(context) is not None:
                return False
            image = context.tasker.controller.cached_image
            if image is None or not _overview_is_clear(context, image):
                return False
            for slot in RECRUITMENT_SLOTS:
                if _slot_outcome(context, slot) == f"DailyRecruitmentSlot{slot}CountLeader":
                    continue
                # Only collected slots become reusable. Other slots are still naturally running.
                state = _overview_slot_state(context, image, slot)
                if state in EMPTY_STATES and not context.set_anchor(
                    f"TkfmRecruitmentSlot{slot}Outcome", "DailyRecruitmentBudgetConfig"
                ):
                    return False
            return True
        except Exception as error:
            print(f"[全境征才] 批次收尾失败：{error}", flush=True)
            return False


@AgentServer.custom_action("DailyRecruitmentPlanVacancies")
class DailyRecruitmentPlanVacancies(CustomAction):
    def run(self, context, argv):
        try:
            _, maximum, entries = _recruitment_budget(context)
            if _pending_slot(context) is not None:
                return False
            image = context.tasker.controller.cached_image
            if image is None or not _overview_is_clear(context, image):
                return False
            states = {slot: _overview_slot_state(context, image, slot) for slot in RECRUITMENT_SLOTS}
            if any(state not in KNOWN_OVERVIEW_STATES for state in states.values()):
                return False
            # Existing ready results already provide vacancies without spending acceleration tickets.
            needed = max(0, maximum - len(entries) - sum(state == "ready" for state in states.values()))
            targets = [slot for slot, state in states.items() if state == "recruiting"][:needed] if maximum > 4 else []
            for slot, state in states.items():
                # A naturally finished slot is collected together with planned accelerated slots.
                reusable = slot in targets or state == "ready"
                value = f"__DailyRecruitmentSlot{slot}CanAccelerate" if reusable else "DailyRecruitmentBudgetConfig"
                if not context.set_anchor(f"TkfmRecruitmentSlot{slot}Accelerate", value):
                    return False
            print(f"[全境征才] 已处理 {len(entries)}/{maximum}，本批仅加速栏位 {targets}；其余自然倒计时", flush=True)
            return True
        except Exception as error:
            print(f"[全境征才] 栏位规划失败：{error}", flush=True)
            return False


def _clean_text(text):
    text = re.sub(r"[^\u3400-\u4dbf\u4e00-\u9fff\uf900-\ufaff]", "", text or "")
    text = text.replace("暗属性", "闇属性").replace("间属性", "闇属性")
    text = text.replace("菁英", "菁英").replace("精英", "菁英")
    text = text.replace("防御者", "守护者")
    text = text.replace("輔助者", "辅助者")
    return text


def _tags_in_text(text):
    text = _clean_text(text)
    return [text] if text in KNOWN_TAGS else []


def _ocr_results(context, image, roi, *, only_rec=True):
    detail = context.run_recognition_direct(
        "OCR",
        JOCR(expected=[], roi=tuple(roi), threshold=0.3, only_rec=only_rec),
        image,
    )
    return list(getattr(detail, "all_results", []) or [])


def _intersects(a, b):
    ax, ay, aw, ah = [int(v) for v in list(a)]
    bx, by, bw, bh = [int(v) for v in list(b)]
    return ax < bx + bw and bx < ax + aw and ay < by + bh and by < ay + ah


def _append_tag(tags, tag, box):
    if any(existing["tag"] == tag for existing in tags):
        return
    x, y, w, h = [int(v) for v in list(box)]
    center_box = [x + w // 2 - 8, y + h // 2 - 8, 16, 16]
    tags.append({"tag": tag, "box": center_box})


def _read_visible_tags(context, image):
    slots = [
        {"slot": index + 1, "roi": list(roi), "texts": [], "candidates": set()}
        for index, roi in enumerate(TAG_SLOT_ROIS)
    ]
    for slot, roi in zip(slots, TAG_SLOT_ROIS):
        for result in _ocr_results(context, image, roi):
            text = getattr(result, "text", "")
            slot["texts"].append({"source": "slot", "text": text, "normalized": _clean_text(text)})
            slot["candidates"].update(_tags_in_text(text))

    for result in _ocr_results(context, image, TAG_ROI, only_rec=False):
        box = getattr(result, "box", None)
        if box is None:
            continue
        matched = [slot for slot in slots if _intersects(box, slot["roi"])]
        if len(matched) != 1:
            continue
        text = getattr(result, "text", "")
        matched[0]["texts"].append({"source": "detected", "text": text, "normalized": _clean_text(text)})
        matched[0]["candidates"].update(_tags_in_text(text))

    tags = []
    for slot in slots:
        slot["candidates"] = sorted(slot["candidates"])
        if len(slot["candidates"]) == 1:
            _append_tag(tags, slot["candidates"][0], slot["roi"])
    return tags, slots


def _read_refresh_count(context, image):
    text = "".join(getattr(result, "text", "") for result in _ocr_results(context, image, REFRESH_ROI))
    numbers = re.findall(r"\d+", text)
    labelled = re.search(r"剩余|剩餘|更换|更換|刷新|更新", text)
    return (int(numbers[0]) if labelled and len(numbers) == 1 else None), text


def _matches(combo):
    combo_set = set(combo)
    matches = []
    for item in RECRUITMENT_POOL:
        if item["stars"] == 3 and "领袖" not in combo_set:
            continue
        if combo_set.issubset(item["tags"]):
            matches.append(item)
    return matches


def _rank_combo(combo, matches):
    stars = [item["stars"] for item in matches]
    return {
        "tags": list(combo),
        "matches": [item["name"] for item in matches],
        "min_stars": min(stars),
        "max_stars": max(stars),
        "candidate_count": len(matches),
        "guaranteed_2_star": min(stars) >= 2,
    }


def _choose_combo(tags):
    available = [entry["tag"] for entry in tags]
    ranked = []
    max_size = min(MAX_SELECTABLE_TAGS, len(available))
    for size in range(max_size, 0, -1):
        for combo in itertools.combinations(available, size):
            matches = _matches(combo)
            if matches:
                ranked.append(_rank_combo(combo, matches))
    if not ranked:
        return None, []

    ranked.sort(
        key=lambda item: (
            item["guaranteed_2_star"],
            item["min_stars"],
            item["max_stars"],
            len(item["tags"]),
            -item["candidate_count"],
        ),
        reverse=True,
    )
    return ranked[0], ranked


def _decision(context, image, slot):
    decision = {
        "action": "wait",
        "reason": "not_setup",
        "visible_tags": [],
        "tag_boxes": {},
        "refresh_count": None,
        "choice": None,
        "ranked": [],
        "slot": slot,
    }
    prompt = context.run_recognition("__DailyRecruitmentBlockingPrompt", image)
    if prompt and prompt.hit:
        decision["reason"] = "blocking_prompt"
        return decision
    setup = context.run_recognition("__DailyRecruitmentSetupReady", image)
    if not setup or not setup.hit:
        return decision

    tags, slots = _read_visible_tags(context, image)
    visible = [entry["tag"] for entry in tags]
    refresh_count, refresh_text = _read_refresh_count(context, image)
    decision.update(
        visible_tags=visible,
        tag_boxes={entry["tag"]: entry["box"] for entry in tags},
        tag_slots=slots,
        refresh_count=refresh_count,
        refresh_text=refresh_text,
    )
    if len(tags) != len(TAG_SLOT_ROIS) or any(len(entry["candidates"]) != 1 for entry in slots):
        decision["reason"] = "incomplete_or_conflicting_tags"
        return decision
    if refresh_count is None:
        decision["reason"] = "unreadable_refresh_count"
        return decision

    choice, ranked = _choose_combo(tags)
    if not choice:
        decision["reason"] = "no_known_combination"
        return decision
    refresh_node = f"DailyRecruitmentSlot{slot}Refresh"
    refresh_limit = (context.get_node_data(refresh_node) or {}).get("max_hit", 3)
    refresh_used = context.get_hit_count(refresh_node)

    if "领袖" in visible:
        action, reason = "skip_leader", "leader_tag_present"
    elif choice["guaranteed_2_star"]:
        action, reason = "recruit_guaranteed", "guaranteed_sr_combination"
    elif refresh_count > 0 and refresh_used < refresh_limit:
        action, reason = "refresh", "no_guarantee_refresh_available"
    else:
        action = "recruit_fallback"
        reason = "refreshes_exhausted" if refresh_count == 0 else "refresh_action_limit_reached"

    decision.update(
        action=action,
        reason=reason,
        decision_basis=reason,
        recruitment_kind=(
            "guaranteed_sr" if action == "recruit_guaranteed"
            else "fallback_1h" if action == "recruit_fallback"
            else action
        ),
        planned_hours=9 if action == "recruit_guaranteed" else 1 if action == "recruit_fallback" else None,
        choice=choice,
        ranked=ranked[:8],
        refresh_used=refresh_used,
        refresh_limit=refresh_limit,
    )
    return decision


def _ensure_task_session(task_id):
    global _SESSION_TASK_ID
    if _SESSION_TASK_ID != task_id:
        _SLOT_SESSIONS.clear()
        _OVERVIEW_OBSERVATIONS.clear()
        _SESSION_TASK_ID = task_id


def _slot_session(task_id, slot):
    _ensure_task_session(task_id)
    return _SLOT_SESSIONS.setdefault(
        slot, {
            "approved": None, "resume_approved": None, "resume_candidate": None,
            "pending_refresh": None, "candidate": None, "candidate_since": 0.0,
        }
    )


def _overview_confirmed(mode: str, states: list[str]) -> bool:
    fingerprint = tuple(states)
    now = time.monotonic()
    candidate = _OVERVIEW_OBSERVATIONS.get(mode)
    if candidate is None or candidate[0] != fingerprint:
        _OVERVIEW_OBSERVATIONS[mode] = (fingerprint, now)
        return False
    return now - candidate[1] >= OVERVIEW_CONFIRM_INTERVAL


def _fingerprint(decision):
    return tuple(decision["visible_tags"]), decision["refresh_count"]


def _wait_for_complete_read(session, decision):
    session["approved"] = None
    if decision["action"] == "wait":
        session["candidate"] = None
        return False
    pending = session["pending_refresh"]
    if pending is not None and decision["refresh_count"] != pending["refresh_count"] - 1:
        decision.update(action="wait", reason="awaiting_refresh_count_change", refresh_before=pending)
        session["candidate"] = None
        return False

    fingerprint = _fingerprint(decision)
    now = time.monotonic()
    if session["candidate"] != fingerprint:
        session.update(candidate=fingerprint, candidate_since=now)
        decision.update(action="wait", reason="awaiting_second_complete_read")
        return False
    if now - session["candidate_since"] < READ_CONFIRM_INTERVAL:
        decision.update(action="wait", reason="awaiting_next_poll")
        return False
    if pending is not None:
        decision["refresh_before"] = pending
        session["pending_refresh"] = None
    session.update(approved=decision, resume_approved=None, resume_candidate=None)
    return True


def _tag_box(decision, index):
    choice = decision.get("choice")
    if not choice or type(index) is not int or not 0 <= index < len(choice["tags"]):
        return None
    box = decision["tag_boxes"].get(choice["tags"][index])
    return tuple(box) if box else None


@AgentServer.custom_recognition("DailyRecruitNoBlockingPrompt")
class DailyRecruitNoBlockingPrompt(CustomRecognition):
    def analyze(self, context, argv):
        try:
            prompt = context.run_recognition("__DailyRecruitmentBlockingPrompt", argv.image)
            if prompt is None:
                return CustomRecognition.AnalyzeResult(box=None, detail={"reason": "prompt_check_failed"})
            if prompt.hit:
                return CustomRecognition.AnalyzeResult(box=None, detail={"reason": "blocking_prompt"})
            # And sub-recognitions do not apply node-level inverse in MaaFramework 5.14.3.
            return CustomRecognition.AnalyzeResult(
                box=(240, 400, 240, 230), detail={"reason": "no_blocking_prompt"}
            )
        except Exception as error:
            return CustomRecognition.AnalyzeResult(
                box=None, detail={"reason": "prompt_check_failed", "error": str(error)}
            )


@AgentServer.custom_action("DailyRecruitmentResetCycle")
class DailyRecruitmentResetCycle(CustomAction):
    def run(self, context, argv):
        global _SESSION_TASK_ID

        try:
            task_id = argv.task_detail.task_id
            clear_hit_count = getattr(context, "clear_hit_count", None)
            if not callable(clear_hit_count):
                print("[全境征才] 无法重置本轮：缺少 clear_hit_count", flush=True)
                return False
            for node_name in _CYCLE_HIT_COUNT_NODES:
                if not clear_hit_count(node_name):
                    print(f"[全境征才] 无法重置本轮节点：{node_name}", flush=True)
                    return False
            # Publish the new cycle only after every navigation/interaction allowance is reset.
            _SLOT_SESSIONS.clear()
            _OVERVIEW_OBSERVATIONS.clear()
            _SESSION_TASK_ID = task_id
            return True
        except Exception as error:
            print(f"[全境征才] 本轮初始化失败：{error}", flush=True)
            return False


def _overview_slot_state(context, image, slot):
    ready = context.run_recognition(f"__DailyRecruitmentSlot{slot}Ready", image)
    if ready and ready.hit:
        return "ready"
    empty = context.run_recognition(f"__DailyRecruitmentSlot{slot}EmptyVisual", image)
    if empty and empty.hit:
        skipped = (
            context.get_hit_count(f"DailyRecruitmentSlot{slot}SkipLeader") > 0
            or _slot_outcome(context, slot) == f"DailyRecruitmentSlot{slot}CountLeader"
        )
        return "skipped_leader" if skipped else "empty"
    occupied = context.run_recognition(f"__DailyRecruitmentSlot{slot}Emergency", image)
    return "recruiting" if occupied and occupied.hit else "unconfirmed"


def _overview_is_clear(context, image):
    title = context.run_recognition("__DailyRecruitmentOverviewTitle", image)
    if not title or not title.hit:
        return False
    clear = context.run_recognition("__DailyRecruitmentNoBlockingPrompt", image)
    return bool(clear and clear.hit)


@AgentServer.custom_recognition("DailyRecruitOverviewComplete")
class DailyRecruitOverviewComplete(CustomRecognition):
    def analyze(self, context, argv):
        try:
            _ensure_task_session(argv.task_detail.task_id)
            if not _overview_is_clear(context, argv.image):
                return CustomRecognition.AnalyzeResult(box=None, detail={"reason": "not_clear_overview"})
            states = [_overview_slot_state(context, argv.image, slot) for slot in RECRUITMENT_SLOTS]
            enabled, maximum, entries = _recruitment_budget(context)
            exhausted = enabled and len(entries) >= maximum
            complete = _pending_slot(context) is None and all(
                state in {"recruiting", "ready", "skipped_leader"} or (exhausted and state == "empty")
                for state in states
            )
            return CustomRecognition.AnalyzeResult(
                box=OVERVIEW_BOX if complete else None,
                detail={"slots": states, "complete": complete},
            )
        except Exception as error:
            return CustomRecognition.AnalyzeResult(
                box=None, detail={"reason": "overview_check_failed", "error": str(error)}
            )


@AgentServer.custom_recognition("DailyRecruitBatchState")
class DailyRecruitBatchState(CustomRecognition):
    """Observe slot states only; Pipeline owns acceleration, collection and page transitions."""

    def analyze(self, context, argv):
        mode = None
        try:
            _ensure_task_session(argv.task_detail.task_id)
            param = _recognition_params(argv.custom_recognition_param)
            if param is None:
                return CustomRecognition.AnalyzeResult(box=None, detail={"reason": "invalid_parameters"})
            mode = param.get("mode")
            if not isinstance(mode, str) or mode not in BATCH_MODES:
                return CustomRecognition.AnalyzeResult(box=None, detail={"reason": "invalid_mode"})
            if not _overview_is_clear(context, argv.image):
                _OVERVIEW_OBSERVATIONS.pop(mode, None)
                return CustomRecognition.AnalyzeResult(box=None, detail={"reason": "not_clear_overview", "mode": mode})
            if mode == "cycle_active":
                active = context.get_hit_count("DailyRecruitmentCycleGate") > 0
                return CustomRecognition.AnalyzeResult(
                    box=OVERVIEW_BOX if active else None, detail={"mode": mode, "active": active}
                )
            if mode == "slot_settled":
                slot = param.get("slot")
                if not _valid_slot(slot):
                    return CustomRecognition.AnalyzeResult(box=None, detail={"reason": "invalid_slot"})
                state = _overview_slot_state(context, argv.image, slot)
                hit = _budget_slot_settled(context, slot, state)
                detail = {"mode": mode, "slot": slot, "state": state, "settled": hit}
            else:
                states = [_overview_slot_state(context, argv.image, slot) for slot in RECRUITMENT_SLOTS]
                if mode == "batch_ready":
                    hit = "ready" in states and all(
                        _budget_slot_settled(context, slot, state)
                        for slot, state in zip(RECRUITMENT_SLOTS, states)
                    )
                elif mode == "all_skipped":
                    hit = all(state == "skipped_leader" for state in states)
                elif mode == "collected":
                    collected = context.get_hit_count("DailyRecruitmentImmediateCollectResults") > 0
                    closed = context.get_hit_count("DailyRecruitmentImmediateCollectClose") > 0
                    hit = collected and closed and all(
                        state in EMPTY_STATES or (state == "recruiting" and not _planned_acceleration(context, slot))
                        for slot, state in zip(RECRUITMENT_SLOTS, states)
                    )
                else:  # entry_collected: another recruiting slot may finish while the summary is open.
                    closed = context.get_hit_count("DailyRecruitmentCollectClose") > 0
                    hit = closed and all(state in KNOWN_OVERVIEW_STATES for state in states)
                detail = {"mode": mode, "slots": states, "complete": hit}
                if mode == "collected":
                    detail.update(collection_started=collected, summary_closed=closed)
                elif mode == "entry_collected":
                    detail["summary_closed"] = closed
                if mode in {"collected", "all_skipped"}:
                    if hit:
                        hit = _overview_confirmed(mode, states)
                        detail.update(complete=hit, reason="confirmed" if hit else "awaiting_second_overview_read")
                    else:
                        _OVERVIEW_OBSERVATIONS.pop(mode, None)
            return CustomRecognition.AnalyzeResult(box=OVERVIEW_BOX if hit else None, detail=detail)
        except Exception as error:
            if isinstance(mode, str):
                _OVERVIEW_OBSERVATIONS.pop(mode, None)
            return CustomRecognition.AnalyzeResult(
                box=None, detail={"reason": "batch_state_check_failed", "error": str(error)}
            )


_DECISION_REASONS = {
    "not_setup": "尚未确认招募设置页",
    "blocking_prompt": "页面存在阻挡弹窗",
    "incomplete_or_conflicting_tags": "五个词条未读齐或有歧义，暂停动作",
    "unreadable_refresh_count": "刷新余量未读清，暂停动作",
    "no_known_combination": "当前角色池无法解释词条组合，暂停动作",
    "leader_tag_present": "出现领袖词条，保留该栏位并跳过",
    "guaranteed_sr_combination": "当前角色池中该组合最低为2星（SR），采用9小时保底招募",
    "no_guarantee_refresh_available": "当前组合不保底SR，且刷新余量与本栏额度均充足，先刷新",
    "refreshes_exhausted": "剩余刷新次数为0，采用默认1小时招募且不选择词条",
    "refresh_action_limit_reached": "本栏刷新动作额度已用尽，采用默认1小时招募且不选择词条",
    "awaiting_second_complete_read": "等待第二次完整一致的词条读取",
    "awaiting_next_poll": "等待下一次周期识别",
    "awaiting_refresh_count_change": "刷新后尚未确认次数恰好下降一次，禁止重复刷新",
    "awaiting_selection_revalidation": "词条已恢复，等待再次确认原组合后从当前选词步骤继续",
    "unexpected_selected_tags": "发现不属于目标组合的已选词条，禁止提交",
    "awaiting_selected_tags": "目标词条尚未全部选中，继续当前选词步骤",
    "tag_already_selected": "目标词条已选中，避免重复点击取消",
    "tag_not_selected": "目标词条尚未选中，等待对应点击分支",
    "approved_snapshot_changed": "当前完整词条或刷新次数与原批准快照不符，禁止继续提交",
    "awaiting_approved_read": "尚无完整且已批准的词条快照",
}


def _selected_tags(context, image, decision):
    selected = []
    for slot in decision["tag_slots"]:
        result = context.run_recognition(f"__DailyRecruitmentTag{slot['slot']}Selected", image)
        if result is None:
            raise RuntimeError("无法检查词条选中状态")
        if result.hit:
            selected.extend(slot["candidates"])
    return selected


def _selection_approval(session, decision):
    approved = session["approved"] or session["resume_approved"]
    if session["pending_refresh"] is not None or approved is None:
        return False, "awaiting_approved_read"
    if decision["action"] == "wait":
        session.update(approved=None, resume_approved=approved, resume_candidate=None)
        return False, decision["reason"]
    if _fingerprint(decision) != _fingerprint(approved) or decision["action"] != approved["action"]:
        session.update(approved=None, resume_approved=None, resume_candidate=None)
        return False, "approved_snapshot_changed"
    if session["approved"] is not None:
        return True, None
    fingerprint = _fingerprint(decision)
    now = time.monotonic()
    candidate = session["resume_candidate"]
    if candidate is None or candidate[0] != fingerprint:
        session["resume_candidate"] = (fingerprint, now)
        return False, "awaiting_selection_revalidation"
    if now - candidate[1] < READ_CONFIRM_INTERVAL:
        return False, "awaiting_selection_revalidation"
    session.update(approved=approved, resume_approved=None, resume_candidate=None)
    return True, None


def _decision_result(box, decision, mode):
    choice = decision.get("choice") or {}
    decision["mode"] = mode
    decision["allowed"] = box is not None
    readings = [
        f"槽{slot['slot']}=" + "/".join(
            f"{entry['text']}→{entry['normalized']}" for entry in slot["texts"]
        ) for slot in decision.get("tag_slots", [])
    ]
    reason = _DECISION_REASONS.get(decision.get("reason"), decision.get("reason", "未知"))
    basis = _DECISION_REASONS.get(decision.get("decision_basis"), "尚未形成有效组合")
    requested = decision.get("requested_tag")
    action = {
        "wait": "等待识别", "skip_leader": "保留领袖词条并跳过本栏", "refresh": "刷新词条",
        "recruit_guaranteed": "9小时SR以上保底征才", "recruit_fallback": "默认1小时征才（不选词条）",
    }.get(decision["action"], decision["action"])
    selected = decision.get("selected_tags", "本步骤未检查选中状态")
    decision["decision_log"] = (
        f"第{decision['slot']}栏；步骤={mode}；识别原文及清洗=" + "；".join(readings)
        + f"；完整词条={decision.get('visible_tags', [])}；刷新原文={decision.get('refresh_text', '未读取')}"
        + f"；刷新余量={decision.get('refresh_count')}"
        + f"；刷新动作={decision.get('refresh_used', 0)}/{decision.get('refresh_limit', 3)}"
        + f"；候选组合={choice.get('tags', [])}；候选角色={choice.get('matches', [])}"
        + f"；最低星级={choice.get('min_stars')}；SR保底={choice.get('guaranteed_2_star', False)}"
        + f"；已选={selected}；本次拟选={requested or '无'}；本次决策={action}"
        + f"；计划小时={decision.get('planned_hours')}；决策依据={basis}；当前原因={reason}"
        + ("；允许当前步骤" if box is not None else "；本次不执行动作")
    )
    return CustomRecognition.AnalyzeResult(box=box, detail=decision)


@AgentServer.custom_recognition("DailyRecruitDecision")
class DailyRecruitDecision(CustomRecognition):
    def analyze(self, context, argv):
        try:
            return self._analyze(context, argv)
        except Exception as error:
            for session in _SLOT_SESSIONS.values():
                session["resume_approved"] = session["approved"] or session["resume_approved"]
                session.update(approved=None, resume_candidate=None, candidate=None)
            return CustomRecognition.AnalyzeResult(
                box=None, detail={"reason": "recognition_error", "error": str(error),
                                  "decision_log": "征才识别异常，暂停当前步骤：" + str(error)}
            )

    def _analyze(self, context, argv):
        param = _recognition_params(argv.custom_recognition_param)
        if param is None:
            return CustomRecognition.AnalyzeResult(box=None, detail={"reason": "invalid_parameters"})
        mode = param.get("mode", "ready")
        if not isinstance(mode, str) or mode not in DECISION_MODES:
            return CustomRecognition.AnalyzeResult(box=None, detail={"reason": "invalid_mode"})
        slot = param.get("slot")
        if not _valid_slot(slot):
            return CustomRecognition.AnalyzeResult(box=None, detail={"reason": "invalid_slot"})
        session = _slot_session(argv.task_detail.task_id, slot)
        index = param.get("index", 0)
        if mode in {"select_tag", "tag_selected"} and (type(index) is not int or not 0 <= index < MAX_SELECTABLE_TAGS):
            session.update(approved=None, resume_approved=None, resume_candidate=None, candidate=None)
            return CustomRecognition.AnalyzeResult(box=None, detail={"reason": "invalid_tag_index"})
        decision = _decision(context, argv.image, slot)
        if session["pending_refresh"] is not None:
            decision["refresh_before"] = session["pending_refresh"]
        box = None
        if mode == "ready":
            if _wait_for_complete_read(session, decision):
                box = (95, 440, 530, 165)
        else:
            allowed, reason = _selection_approval(session, decision)
            if not allowed:
                decision.update(action="wait", reason=reason)
            elif mode == decision["action"]:
                box = (95, 440, 530, 165)
                if mode == "refresh":
                    session["pending_refresh"] = {
                        "visible_tags": list(decision["visible_tags"]),
                        "refresh_count": decision["refresh_count"],
                    }
                    session.update(approved=None, resume_approved=None, resume_candidate=None, candidate=None)
            elif mode == "prepare" and decision["action"] in {"recruit_guaranteed", "recruit_fallback"}:
                box = (95, 440, 530, 165)
            elif mode == "submit" and decision["action"] == "recruit_fallback":
                # The game defaults to one hour; fallback must not select tags or wait for a time template.
                decision["selected_tags"] = []
                box = (390, 1000, 220, 90)
            elif (
                mode in {"select_tag", "tag_selected", "selection_complete", "submit"}
                and decision["action"] == "recruit_guaranteed"
            ):
                selected = _selected_tags(context, argv.image, decision)
                expected = decision["choice"]["tags"]
                decision["selected_tags"] = selected
                if set(selected) - set(expected):
                    decision["reason"] = "unexpected_selected_tags"
                elif mode in {"submit", "selection_complete"}:
                    if set(selected) == set(expected):
                        box = (390, 1000, 220, 90)
                    else:
                        decision["reason"] = "awaiting_selected_tags"
                elif index < len(expected):
                    tag = expected[index]
                    decision["requested_tag"] = tag
                    if mode == "tag_selected" and tag in selected:
                        box = _tag_box(decision, index)
                    elif mode == "select_tag" and tag not in selected:
                        box = _tag_box(decision, index)
                    else:
                        decision["reason"] = "tag_already_selected" if tag in selected else "tag_not_selected"
        return _decision_result(box, decision, mode)
