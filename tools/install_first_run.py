"""Install the Windows pre-configuration startup hook without changing frontend binaries."""

import argparse
import json
import shutil
from pathlib import Path


def install_first_run(runtime: Path, assembly: Path):
    source = Path(__file__).resolve().parent / "first_run"
    if not assembly.is_file():
        raise FileNotFoundError(f"缺少迁移启动模块：{assembly}")
    for name in ("MFAAvalonia.runtimeconfig.json", "MFAAvalonia.deps.json"):
        if not (runtime / name).is_file():
            raise FileNotFoundError(f"缺少前端启动配置：{runtime / name}")
    shutil.copy2(assembly, runtime / "TkfmFirstRun.dll")
    destination = runtime / "migration"
    destination.mkdir(exist_ok=True)
    for name in ("first_run.ps1", "import_config.py"):
        # Windows PowerShell 5.1 requires a BOM for Chinese script literals.
        (destination / name).write_text(
            (source / name).read_text(encoding="utf-8-sig"),
            encoding="utf-8-sig" if name.endswith(".ps1") else "utf-8", newline="\n",
        )
    deps_path = runtime / "MFAAvalonia.deps.json"
    deps = json.loads(deps_path.read_text(encoding="utf-8-sig"))
    installed = False
    for target in deps["targets"].values():
        frontend = next((value for key, value in target.items() if key.startswith("MFAAvalonia/")), None)
        if frontend is None:
            continue  # Some frontend packages also contain an empty portable target.
        frontend.setdefault("dependencies", {})["TkfmFirstRun"] = "1.0.0"
        target["TkfmFirstRun/1.0.0"] = {"runtime": {"TkfmFirstRun.dll": {}}}
        installed = True
    if not installed:
        raise ValueError("前端依赖配置中找不到 MFAAvalonia 主程序")
    deps["libraries"]["TkfmFirstRun/1.0.0"] = {"type": "project", "serviceable": False, "sha512": ""}
    deps_path.write_text(json.dumps(deps, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    config_path = runtime / "MFAAvalonia.runtimeconfig.json"
    config = json.loads(config_path.read_text(encoding="utf-8-sig"))
    props = config["runtimeOptions"].setdefault("configProperties", {})
    hooks = [item for item in props.get("STARTUP_HOOKS", "").split(";") if item]
    if "TkfmFirstRun" not in hooks:
        hooks.append("TkfmFirstRun")
    props["STARTUP_HOOKS"] = ";".join(hooks)
    config_path.write_text(json.dumps(config, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runtime", type=Path, required=True)
    parser.add_argument("--assembly", type=Path, required=True)
    args = parser.parse_args()
    install_first_run(args.runtime, args.assembly)
