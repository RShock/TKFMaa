from bootstrap import configure_runtime

configure_runtime()

from maa.agent.agent_server import AgentServer
from maa.custom_action import CustomAction
from maa.custom_recognition import CustomRecognition

from common import safe_json_loads


SHOP_COLUMNS = [
    ("left", 70, 182, 84),
    ("middle", 255, 180, 265),
    ("right", 435, 180, 446),
]

_HANDLED_ITEM_KEYS = set()
_PENDING_TARGET = None
_SCAN_STATES = {}
MAX_DOWN_SCROLLS = 6


def _scan_state(shop_id):
    return _SCAN_STATES.setdefault(shop_id, {"position": 0, "empty_scrolls": 0, "has_targets": False})


def _custom_parameters(raw_param):
    if not isinstance(raw_param, (dict, str)):
        return {}
    param = raw_param if isinstance(raw_param, dict) else safe_json_loads(raw_param, {})
    return param if isinstance(param, dict) else {}


def _card_click_box(slot):
    x, y, width, height = slot["click_box"]
    return (x + width // 2 - 8, y + height // 2 - 8, 16, 16)


def _selected_targets(context, node_names):
    """Read enabled image targets; no OCR fallback is allowed for shop items."""
    if not isinstance(node_names, list):
        return []
    targets = []
    for node_name in node_names:
        if not isinstance(node_name, str):
            continue
        node = context.get_node_data(node_name)
        if not isinstance(node, dict) or node.get("enabled") is not True:
            continue
        config = node.get("attach", {})
        recognition = node.get("recognition", {})
        if not isinstance(config, dict) or not isinstance(recognition, dict):
            continue
        name = config.get("target_name")
        if not isinstance(name, str) or not name or recognition.get("type") != "TemplateMatch":
            continue
        targets.append(
            {
                "node": node_name,
                "raw": name,
                "discount_node": config.get("discount_node"),
            }
        )
    return targets


def _recognition_score(result):
    scores = [
        score
        for entry in (getattr(result, "all_results", None) or [])
        if isinstance(score := getattr(entry, "score", None), (int, float))
    ]
    return max(scores, default=None)


def _box_tuple(box):
    if isinstance(box, (list, tuple)) and len(box) == 4:
        try:
            return tuple(int(value) for value in box)
        except (TypeError, ValueError):
            return None
    return None


def _purchase_button_match(context, image):
    if not _PENDING_TARGET:
        return CustomRecognition.AnalyzeResult(box=None, detail={"reason": "no_pending_item"})

    result = context.run_recognition("__DailyShopPurchaseButton", image)
    if result is None or not result.hit:
        return CustomRecognition.AnalyzeResult(
            box=None,
            detail={"reason": "purchase_button_not_found", "recognition_started": result is not None},
        )

    matches = list(getattr(result, "filtered_results", []) or [])
    if not matches:
        fallback = _box_tuple(getattr(result, "box", None))
        if fallback is None:
            return CustomRecognition.AnalyzeResult(
                box=None, detail={"reason": "purchase_button_box_missing"}
            )
        return CustomRecognition.AnalyzeResult(
            box=fallback,
            detail={"reason": "purchase_button_matched", "score": _recognition_score(result)},
        )

    best = max(
        matches,
        key=lambda entry: getattr(entry, "score", 0)
        if isinstance(getattr(entry, "score", 0), (int, float))
        else 0,
    )
    box = _box_tuple(getattr(best, "box", None))
    if box is None:
        return CustomRecognition.AnalyzeResult(
            box=None, detail={"reason": "purchase_button_box_invalid"}
        )
    return CustomRecognition.AnalyzeResult(
        box=box,
        detail={
            "reason": "purchase_button_matched",
            "score": getattr(best, "score", None),
            "target": _PENDING_TARGET["target"],
            "selection_method": "TemplateMatch",
        },
    )


def _visible_slots(context, image, shop_id):
    if shop_id not in ("exchange", "guild"):
        return []
    node_name = "__DailyShopExchangeCurrency" if shop_id == "exchange" else "__DailyShopGuildCurrency"
    result = context.run_recognition(node_name, image)
    anchors = sorted(getattr(result, "filtered_results", []) or [], key=lambda item: (item.box[1], item.box[0]))
    slots = {}
    row_y = None
    row_index = -1
    for anchor in anchors:
        y = anchor.box[1] - (332 if shop_id == "exchange" else 330)
        if y < 260 or y + 365 > 1045:
            continue
        if row_y is None or abs(y - row_y) > 10:
            row_y = y
            row_index += 1
        for column, x, width, anchor_x in SHOP_COLUMNS:
            if abs(anchor.box[0] - anchor_x) <= 5:
                roi = (x, y, width, 365)
                name = f"row_{row_index}_{column}"
                if name not in slots or anchor.score > slots[name]["anchor_score"]:
                    slots[name] = {
                        "name": name,
                        "card_roi": roi,
                        "click_box": roi,
                        "anchor_score": anchor.score,
                    }
                break
    return list(slots.values())


def _read_visible_items(context, image, targets, shop_id):
    visible = []
    diagnostics = []
    slots = _visible_slots(context, image, shop_id)
    if not slots:
        diagnostics.append({"reason": "card_anchors_unconfirmed", "shop_id": shop_id})
    for slot in slots:
        matched_nodes = []
        checks = []
        for target in targets:
            node_name = target["node"]
            result = context.run_recognition(
                node_name,
                image,
                {node_name: {"recognition": {"param": {"roi": list(slot["card_roi"])}}}},
            )
            matched = result is not None and result.hit
            check = {
                "target": target["raw"],
                "mode": "TemplateMatch",
                "score": _recognition_score(result),
                "template_hit": matched,
                "recognition_started": result is not None,
            }
            if matched and target["discount_node"]:
                discount_node = target["discount_node"]
                x, y, width, _ = slot["card_roi"]
                discount_result = context.run_recognition(
                    discount_node,
                    image,
                    {discount_node: {"recognition": {"param": {"roi": [x, y, width, 100]}}}},
                )
                matched = discount_result is not None and discount_result.hit
                check["discount_score"] = _recognition_score(discount_result)
                check["discount_hit"] = matched
            check["matched"] = matched
            checks.append(check)
            if matched:
                matched_nodes.append(node_name)
        diagnostics.append(
            {
                "slot": slot["name"],
                "roi": slot["card_roi"],
                "anchor_score": slot["anchor_score"],
                "checks": checks,
            }
        )
        if matched_nodes:
            visible.append(
                {
                    "slot": slot["name"],
                    "matched_nodes": matched_nodes,
                    "box": _card_click_box(slot),
                }
            )
    return visible, diagnostics


def _item_key(shop_id, target, item):
    return (shop_id, target["node"], _scan_state(shop_id)["position"], item["slot"])


def _pending_detail_matches(context, _image):
    if not _PENDING_TARGET:
        return CustomRecognition.AnalyzeResult(box=None, detail={"reason": "no_pending_item"})

    pending = _PENDING_TARGET
    node = context.get_node_data(pending["node"])
    if not isinstance(node, dict) or node.get("enabled") is not True:
        return CustomRecognition.AnalyzeResult(box=None, detail={"reason": "target_not_selected"})
    shop_id, _, position, slot_name = pending["key"]
    return CustomRecognition.AnalyzeResult(
        box=(0, 0, 1, 1),
        detail={
            "shop_id": shop_id,
            "slot": slot_name,
            "position": position,
            "target": pending["target"],
            "selection_method": "TemplateMatch",
            "matched": True,
            "detail_policy": "image_match_buy_directly",
        },
    )


def _analyze_target_item(context, argv, default_shop_id):
    global _PENDING_TARGET

    _PENDING_TARGET = None
    raw_param = argv.custom_recognition_param
    param = raw_param if isinstance(raw_param, dict) else safe_json_loads(raw_param, {})
    if not isinstance(param, dict):
        return CustomRecognition.AnalyzeResult(box=None, detail={"reason": "invalid_parameters"})
    targets = _selected_targets(context, param.get("target_nodes", []))
    shop_id = str(param.get("shop_id", default_shop_id))
    state = _scan_state(shop_id)
    state["has_targets"] = bool(targets)
    if not targets:
        return CustomRecognition.AnalyzeResult(
            box=None, detail={"reason": "no_targets_selected", "shop_id": shop_id}
        )
    raw_targets = [target["raw"] for target in targets]
    visible, diagnostics = _read_visible_items(context, argv.image, targets, shop_id)
    skipped = []

    for target in targets:
        for item in visible:
            if target["node"] not in item["matched_nodes"]:
                continue
            key = _item_key(shop_id, target, item)
            if key in _HANDLED_ITEM_KEYS:
                skipped.append(
                    {
                        "target": target["raw"],
                        "slot": item["slot"],
                        "reason": "already_handled",
                    }
                )
                continue
            _PENDING_TARGET = {
                "key": key,
                "node": target["node"],
                "target": target["raw"],
            }
            return CustomRecognition.AnalyzeResult(
                box=item["box"],
                detail={
                    "target": target["raw"],
                    "selection_method": "TemplateMatch",
                    "shop_id": shop_id,
                    "slot": item["slot"],
                    "position": state["position"],
                    "empty_scrolls": state["empty_scrolls"],
                    "scroll_limit": MAX_DOWN_SCROLLS,
                    "items": [
                        {
                            "slot": entry["slot"],
                            "matched_nodes": entry["matched_nodes"],
                        }
                        for entry in visible
                    ],
                    "targets": raw_targets,
                    "diagnostics": diagnostics,
                    "skipped": skipped,
                    "detail_policy": "image_match_buy_directly",
                },
            )

    return CustomRecognition.AnalyzeResult(
        box=None,
        detail={
            "reason": "no_target_visible",
            "shop_id": shop_id,
            "position": state["position"],
            "empty_scrolls": state["empty_scrolls"],
            "scroll_limit": MAX_DOWN_SCROLLS,
            "items": [
                {
                    "slot": entry["slot"],
                    "matched_nodes": entry["matched_nodes"],
                }
                for entry in visible
            ],
            "targets": raw_targets,
            "diagnostics": diagnostics,
            "skipped": skipped,
        },
    )


@AgentServer.custom_action("DailyShopResetExchangeSession")
class DailyShopResetExchangeSession(CustomAction):
    def run(self, context, argv):
        global _HANDLED_ITEM_KEYS, _PENDING_TARGET, _SCAN_STATES
        _HANDLED_ITEM_KEYS = set()
        _PENDING_TARGET = None
        _SCAN_STATES = {}
        return True


@AgentServer.custom_action("DailyShopMarkScrolled")
class DailyShopMarkScrolled(CustomAction):
    def run(self, context, argv):
        param = _custom_parameters(argv.custom_action_param)
        shop_id = param.get("shop_id")
        if shop_id not in ("exchange", "guild"):
            return False
        # This action runs only after the pipeline swipe succeeds. The screen
        # reached by the sixth swipe is still checked before deciding to stop.
        state = _scan_state(shop_id)
        state["position"] += 1
        state["empty_scrolls"] += 1
        return True


@AgentServer.custom_recognition("DailyShopCanScroll")
class DailyShopCanScroll(CustomRecognition):
    def analyze(self, context, argv):
        param = _custom_parameters(argv.custom_recognition_param)
        shop_id = param.get("shop_id")
        if shop_id not in ("exchange", "guild"):
            return CustomRecognition.AnalyzeResult(box=None, detail={"reason": "invalid_parameters"})
        state = _scan_state(shop_id)
        allowed = state["has_targets"] and state["position"] < MAX_DOWN_SCROLLS
        return CustomRecognition.AnalyzeResult(
            box=(0, 0, 1, 1) if allowed else None,
            detail={
                "reason": (
                    "no_targets_selected"
                    if not state["has_targets"]
                    else "scroll_to_find_target"
                    if allowed
                    else "scroll_limit_reached"
                ),
                "shop_id": shop_id,
                "position": state["position"],
                "empty_scrolls": state["empty_scrolls"],
                "scroll_limit": MAX_DOWN_SCROLLS,
            },
        )


@AgentServer.custom_recognition("DailyShopVerifyPurchaseDetail")
class DailyShopVerifyPurchaseDetail(CustomRecognition):
    def analyze(self, context, argv):
        try:
            return _pending_detail_matches(context, argv.image)
        except Exception as error:
            return CustomRecognition.AnalyzeResult(
                box=None, detail={"reason": "recognition_error", "error": str(error)}
            )


@AgentServer.custom_recognition("DailyShopPurchaseButtonReady")
class DailyShopPurchaseButtonReady(CustomRecognition):
    def analyze(self, context, argv):
        try:
            return _purchase_button_match(context, argv.image)
        except Exception as error:
            return CustomRecognition.AnalyzeResult(
                box=None, detail={"reason": "recognition_error", "error": str(error)}
            )


@AgentServer.custom_action("DailyShopMarkTargetPurchased")
class DailyShopMarkTargetPurchased(CustomAction):
    def run(self, context, argv):
        global _PENDING_TARGET
        if _PENDING_TARGET:
            _HANDLED_ITEM_KEYS.add(_PENDING_TARGET["key"])
            _scan_state(_PENDING_TARGET["key"][0])["empty_scrolls"] = 0
            _PENDING_TARGET = None
        return True


@AgentServer.custom_action("DailyShopSkipPendingTarget")
class DailyShopSkipPendingTarget(CustomAction):
    def run(self, context, argv):
        global _PENDING_TARGET
        if _PENDING_TARGET:
            # Closing an unverified detail must not reopen the same card or
            # count as a successful purchase; the total swipe budget never resets.
            _HANDLED_ITEM_KEYS.add(_PENDING_TARGET["key"])
            _PENDING_TARGET = None
        return True


@AgentServer.custom_recognition("DailyShopExchangeTargetItem")
class DailyShopExchangeTargetItem(CustomRecognition):
    def analyze(self, context, argv):
        try:
            return _analyze_target_item(context, argv, "exchange")
        except Exception as error:
            return CustomRecognition.AnalyzeResult(
                box=None, detail={"reason": "recognition_error", "error": str(error)}
            )


@AgentServer.custom_recognition("DailyShopGuildTargetItem")
class DailyShopGuildTargetItem(CustomRecognition):
    def analyze(self, context, argv):
        try:
            return _analyze_target_item(context, argv, "guild")
        except Exception as error:
            return CustomRecognition.AnalyzeResult(
                box=None, detail={"reason": "recognition_error", "error": str(error)}
            )
