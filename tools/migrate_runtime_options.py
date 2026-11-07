"""Preserve saved task choices when retiring startup and immediate-recruit switches."""

import argparse
import copy
import json
import shutil
from pathlib import Path


def find_count(options):
    for option in options:
        if option.get("name") == "全境征才循环次数":
            return option
        nested = find_count(option.get("sub_options", []))
        if nested is not None:
            return nested
    return None


def migrate(config):
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
