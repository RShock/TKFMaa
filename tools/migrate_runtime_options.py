"""Preserve task choices and use one controller for task and preview capture."""

import argparse
import copy
import json
import shutil
from pathlib import Path


SHOP_GROUPS = (
    ("指定购买物品", "指定购买物品", ("招募命令", "紧急录用证（打折）", "初级体力药水")),
    ("交易所材料", "指定购买物品", ("初级材料（打折）", "中级材料（打折）")),
    (
        "协会属性石",
        "指定协会商店购买物品",
        ("烈风之石（打折）", "光明之石（打折）", "黑暗之石（打折）", "火焰之石（打折）", "洪水之石"),
    ),
    (
        "协会调教道具",
        "指定协会商店购买物品",
        ("藤蔓（打折）", "淫魔虫（打折）", "电击棒（打折）", "史莱姆（打折）", "八目鳗（打折）"),
    ),
    ("指定协会商店购买物品", "指定协会商店购买物品", ("召唤契约", "阿克色记录", "银币（打折）")),
)
SHOP_DEFAULTS = {
    "指定购买物品": ["招募命令", "初级体力药水"],
    "指定协会商店购买物品": ["召唤契约", "阿克色记录"],
}


def migrate_shop_groups(config):
    if config.get("TkfmShopGroupsV1") is True:
        return
    interface_path = Path(__file__).resolve().parent.parent / "assets/interface.json"
    interface = json.loads(interface_path.read_text(encoding="utf-8"))
    target_nodes = {
        node
        for group, _, _ in SHOP_GROUPS
        for case in interface["option"][group]["cases"]
        for node in case.get("pipeline_override", {})
    }
    group_keys = {key for group, _, _ in SHOP_GROUPS for key in (group, group + "仅购买打折商品")}
    for task in config.get("TaskItems", []):
        if task.get("entry") != "DailyShopStart":
            continue
        previous = {option.get("name"): option for option in task.get("option", [])}
        options = [option for option in task.get("option", []) if option.get("name") not in group_keys]
        for group, source, names in SHOP_GROUPS:
            saved = previous.get(group, previous.get(source, {}))
            selected = saved.get("selected_cases")
            if not isinstance(selected, list):
                selected = SHOP_DEFAULTS.get(source, [])
            options.append({
                "name": group,
                "index": 0,
                "data": {},
                "selected_cases": [name for name in names if name in selected],
            })
            switch = group + "仅购买打折商品"
            options.append(copy.deepcopy(previous.get(switch, {"name": switch, "index": 0})))
        task["option"] = options
        # Cached task overrides must not resurrect unchecked products after regrouping.
        overrides = task.get("pipeline_override", {})
        for node in target_nodes:
            value = overrides.get(node)
            if isinstance(value, dict):
                value.pop("enabled", None)
                if not value:
                    overrides.pop(node, None)
    config["TkfmShopGroupsV1"] = True


def find_count(options):
    for option in options:
        if option.get("name") == "全境征才循环次数":
            return option
        nested = find_count(option.get("sub_options", []))
        if nested is not None:
            return nested
    return None


def migrate(config):
    # This also applies to instances whose task choices were already migrated.
    config["UseSeparateScreenshotTasker"] = False
    migrate_shop_groups(config)
    if config.get("TkfmGameOptionsV2") is True:
        return
    tasks = config.get("TaskItems", [])
    login = next((task for task in tasks if task.get("entry") == "GameLoginStart"), {})
    server = next((o for o in login.get("option", []) if o.get("name") == "选择服务器"), {})
    if config.get("Resource", "官服") == "官服" and server.get("index") == 1:
        config["Resource"] = "erolab服务器"
    config["TaskItems"] = [task for task in tasks if task.get("entry") != "GameLoginStart"]
    config["CurrentTasks"] = [
        name for name in config.get("CurrentTasks", []) if not name.endswith("<|||>GameLoginStart")
    ]
    for task in config["TaskItems"]:
        if task.get("entry") != "DailyRecruitmentStart":
            continue
        options = task.get("option", [])
        count = copy.deepcopy(find_count(options)) or {"name": "全境征才循环次数", "data": {"次数": "4"}}
        count.pop("pipeline_override", None)
        task["option"] = [
            o for o in options if o.get("name") not in {"全境征才立即招募", "全境征才循环次数"}
        ] + [count]
        overrides = task.get("pipeline_override", {})
        for node in ("DailyRecruitmentCycleGate", "DailyRecruitmentAfterSlots", "DailyRecruitmentCollectResults"):
            overrides.pop(node, None)
        overrides.get("DailyRecruitmentBudgetConfig", {}).get("attach", {}).pop("immediate", None)
    config["TkfmGameOptionsV2"] = True


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--instances", type=Path, required=True)
    parser.add_argument("--backup", type=Path, required=True)
    args = parser.parse_args()
    changed = 0
    for path in sorted(args.instances.glob("*.json")):
        config = json.loads(path.read_text(encoding="utf-8-sig"))
        before = copy.deepcopy(config)
        migrate(config)
        if config == before:
            continue
        args.backup.mkdir(parents=True, exist_ok=True)
        shutil.copy2(path, args.backup / path.name)
        path.write_text(json.dumps(config, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        changed += 1
    print(f"已迁移 {changed} 份实例配置（原配置已备份）")


if __name__ == "__main__":
    main()
