"""Import persisted user data from the exact old Windows process selected by the user."""

import argparse
import ctypes
from ctypes import wintypes
import hashlib
import json
import os
from pathlib import Path
import shutil
import sys
from datetime import datetime
import uuid


def inside(path, root):
    return path.resolve().is_relative_to(root.resolve())


def plain_path(path):
    if path.is_symlink() or getattr(path.lstat(), "st_file_attributes", 0) & 0x400:
        raise ValueError(f"不迁移符号链接或目录联接：{path}")


def plain_destination(path, root):
    if not path.absolute().is_relative_to(root.absolute()):
        raise ValueError("迁移目标目录越界")
    for item in [path, *path.parents]:
        if item.exists():
            plain_path(item)
        if item == root:
            break


def read_json(path):
    import jsonc

    with path.open(encoding="utf-8-sig") as stream:
        return jsonc.load(stream)


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").digest()


def copy_checked(source, target):
    plain_path(source)
    if source.is_dir():
        target.mkdir(parents=True, exist_ok=True)
        for child in source.iterdir():
            copy_checked(child, target / child.name)
    else:
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, target)
        if digest(source) != digest(target):
            raise OSError(f"旧程序仍在写入或文件复制不完整：{source}")


def remove_owned(path, root):
    if not inside(path, root) or path.resolve() == root.resolve():
        raise ValueError("拒绝删除迁移工作目录以外的数据")
    if path.is_dir():
        plain_path(path)
        shutil.rmtree(path)
    elif path.exists():
        path.unlink()


class OldProcess:
    def __init__(self, process_id, executable, started):
        self.kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        self.kernel.OpenProcess.argtypes = (wintypes.DWORD, wintypes.BOOL, wintypes.DWORD)
        self.kernel.OpenProcess.restype = wintypes.HANDLE
        self.kernel.CloseHandle.argtypes = (wintypes.HANDLE,)
        self.kernel.QueryFullProcessImageNameW.argtypes = (
            wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)
        )
        self.kernel.GetProcessTimes.argtypes = (wintypes.HANDLE,) + (ctypes.POINTER(wintypes.FILETIME),) * 4
        self.kernel.TerminateProcess.argtypes = (wintypes.HANDLE, wintypes.UINT)
        self.kernel.WaitForSingleObject.argtypes = (wintypes.HANDLE, wintypes.DWORD)
        self.kernel.WaitForSingleObject.restype = wintypes.DWORD
        self.handle = self.kernel.OpenProcess(0x1000 | 0x100000 | 1, False, process_id)
        if not self.handle:
            raise ctypes.WinError(ctypes.get_last_error())
        try:
            name = ctypes.create_unicode_buffer(32768)
            size = wintypes.DWORD(len(name))
            if not self.kernel.QueryFullProcessImageNameW(self.handle, 0, name, ctypes.byref(size)):
                raise ctypes.WinError(ctypes.get_last_error())
            times = [wintypes.FILETIME() for _ in range(4)]
            if not self.kernel.GetProcessTimes(self.handle, *(ctypes.byref(value) for value in times)):
                raise ctypes.WinError(ctypes.get_last_error())
            actual_start = times[0].dwLowDateTime | times[0].dwHighDateTime << 32
            if Path(name.value).resolve() != executable or actual_start != started:
                raise ValueError("旧进程身份已改变，请刷新列表后重新选择")
        except Exception:
            self.close()
            raise

    def stop(self):
        # Retain the verified handle, so a recycled PID can never target another app.
        if self.kernel.WaitForSingleObject(self.handle, 0) == 0:
            return
        if not self.kernel.TerminateProcess(self.handle, 0):
            raise ctypes.WinError(ctypes.get_last_error())
        if self.kernel.WaitForSingleObject(self.handle, 10000) != 0:
            raise OSError("旧助手未能退出，未写入新版配置")

    def close(self):
        if self.handle:
            self.kernel.CloseHandle(self.handle)
            self.handle = None


def strings(value):
    if isinstance(value, str):
        yield value
        if value.lstrip().startswith(("{", "[")):
            try:
                yield from strings(json.loads(value))
            except (ValueError, TypeError):
                pass
    elif isinstance(value, dict):
        for child in value.values():
            yield from strings(child)
    elif isinstance(value, list):
        for child in value:
            yield from strings(child)


def relocate(value, old_root, new_root):
    if isinstance(value, dict):
        return {key: relocate(child, old_root, new_root) for key, child in value.items()}
    if isinstance(value, list):
        return [relocate(child, old_root, new_root) for child in value]
    if not isinstance(value, str):
        return value
    if value.lstrip().startswith(("{", "[")):
        try:
            parsed = json.loads(value)
            changed = relocate(parsed, old_root, new_root)
            return json.dumps(changed, ensure_ascii=False) if changed != parsed else value
        except (ValueError, TypeError):
            return value
    normalized = value.replace("/", "\\")
    prefix = str(old_root).rstrip("\\")
    if normalized.lower() == prefix.lower() or normalized.lower().startswith(prefix.lower() + "\\"):
        return str(new_root) + normalized[len(prefix):]
    return value


def resource_roots(install_root, interface):
    roots = [install_root / "resource"]
    for resource in interface.get("resource", []):
        for raw in resource.get("path", []):
            if not isinstance(raw, str):
                continue
            path = Path(raw.replace("{PROJECT_DIR}", str(install_root)))
            roots.append(path if path.is_absolute() else install_root / path)
    return roots


def resource_images(old_root, interface):
    result = []
    for root in resource_roots(old_root, interface):
        image = root.resolve() / "image" / "UserStages"
        if image.is_dir() and image not in result:
            # Reject junctions even when resolving the resource's parent path.
            plain_path(root)
            plain_path(root / "image")
            plain_path(root / "image" / "UserStages")
            result.append(image)
    return result


def stage(old_root, new_root, interface, destination):
    if destination.exists():
        remove_owned(destination, destination.parent)
    destination.mkdir()
    config = old_root / "config"
    if not config.is_dir():
        raise ValueError("旧目录缺少 config，无法导入")
    copy_checked(config, destination / "config")
    global_settings = old_root / "appsettings.json"
    if global_settings.is_file():
        copy_checked(global_settings, destination / "appsettings.json")
    templates = destination / "resource" / "image" / "UserStages"
    templates.mkdir(parents=True)
    for folder in resource_images(old_root, interface):
        for source in folder.rglob("*"):
            plain_path(source)
            if source.is_file():
                relative = source.relative_to(folder)
                target = templates / relative
                if target.exists() and digest(source) != digest(target):
                    raise ValueError(f"多个旧资源中存在不同的同名截图：{relative}")
        copy_checked(folder, templates)
    config_files = list((destination / "config").rglob("*.json"))
    if (destination / "appsettings.json").is_file():
        config_files.append(destination / "appsettings.json")
    references = set()
    for config_file in config_files:
        data = read_json(config_file)
        for value in strings(data):
            relative = value.replace("\\", "/")
            if relative.startswith("UserStages/"):
                image = templates.parent / relative
                if not inside(image, templates) or not image.is_file():
                    raise ValueError(f"关卡截图缺失，已停止迁移：{relative}")
                references.add(relative)
        changed = relocate(data, old_root, new_root)
        if changed != data:
            config_file.write_text(json.dumps(changed, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    if not any((destination / "config" / "instances").glob("*.json")):
        raise ValueError("旧目录没有已保存的实例配置，请先在旧助手中保存设置")
    return len(config_files), sum(path.is_file() for path in templates.rglob("*")), len(references)


def apply_payload(payload, new_root, backup, image_target):
    items = [Path("config"), Path("resource/image/UserStages")]
    if (payload / "appsettings.json").is_file():
        items.append(Path("appsettings.json"))
    # Keep any newer user images that have no corresponding source file.
    current_images = image_target
    plain_destination(current_images, image_target.parent)
    if current_images.is_dir():
        for source in current_images.rglob("*"):
            plain_path(source)
            if source.is_file():
                target = payload / "resource/image/UserStages" / source.relative_to(current_images)
                if not target.exists():
                    copy_checked(source, target)
    journal = []
    try:
        for relative in items:
            target = image_target if relative == Path("resource/image/UserStages") else new_root / relative
            owner = image_target.parent if target == image_target else new_root
            saved = backup / relative
            # Resolve and reject junctions before touching any destination.
            if not inside(target, owner) or not inside(saved, backup):
                raise ValueError("迁移目标目录越界")
            plain_destination(target, owner)
            plain_destination(saved, new_root)
            existed = target.exists()
            saved.parent.mkdir(parents=True, exist_ok=True)
            journal.append((target, saved, existed, owner))
            if existed:
                os.replace(target, saved)
            target.parent.mkdir(parents=True, exist_ok=True)
            os.replace(payload / relative, target)
    except Exception:
        failures = []
        for target, saved, existed, owner in reversed(journal):
            try:
                if saved.exists() or not existed:
                    remove_owned(target, owner)
                if saved.exists():
                    os.replace(saved, target)
            except Exception as error:
                failures.append(str(error))
        if failures:
            raise OSError(f"导入失败且自动恢复未完成，请从 {backup} 恢复：{'；'.join(failures)}")
        raise


def main(args):
    if os.name != "nt":
        raise RuntimeError("进程定位迁移仅适用于 Windows")
    old_exe = args.source_exe.resolve()
    old_root, new_root = old_exe.parent, args.target.resolve()
    if args.process_id == args.current_process_id or inside(old_root, new_root) or inside(new_root, old_root):
        raise ValueError("不能从当前程序或相互包含的目录迁移")
    plain_path(old_root)
    plain_path(new_root)
    interface = read_json(old_root / "interface.json")
    if interface.get("name", "").replace("（开发版）", "") not in {"TKFMaa", "TKFMaaR"}:
        raise ValueError("所选进程不是 TKFMaa/TKFMaaR 助手")
    current_interface = read_json(new_root / "interface.json")
    new_resource = next((root for root in reversed(resource_roots(new_root, current_interface))
                         if (root / "custom/TkfmCustomStage.cs").is_file()), new_root / "resource")
    if new_resource.exists():
        plain_path(new_resource)
    image_target = new_resource.resolve() / "image/UserStages"
    if any(inside(folder, image_target) or inside(image_target, folder)
           for folder in resource_images(old_root, interface)):
        raise ValueError("旧版与新版共用截图目录，无需迁移；请先将新版放到独立目录")
    process = OldProcess(args.process_id, old_exe, args.started)
    token = datetime.now().strftime("%Y%m%d-%H%M%S-") + uuid.uuid4().hex[:8]
    work = new_root / "temp" / ("config-import-" + token)
    backup = new_root / "backup" / ("config-import-" + token)
    try:
        plain_destination(work, new_root)
        plain_destination(backup, new_root)
        work.mkdir(parents=True)
        payload = work / "payload"
        stage(old_root, new_root, interface, payload)
        process.stop()
        counts = stage(old_root, new_root, interface, payload)
        backup.mkdir(parents=True)
        result = {"ok": True, "config_files": counts[0], "images": counts[1],
                  "references": counts[2], "backup": str(backup), "source": str(old_root)}
        # Write supporting metadata before committing: a later log failure must
        # never report a successful import as if it had rolled back.
        (backup / "migration.json").write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
        apply_payload(payload, new_root, backup, image_target)
        return result
    finally:
        process.close()
        if work.exists():
            try:
                remove_owned(work, new_root)
            except OSError:
                pass  # Retained staging data does not change a committed result.


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-exe", type=Path, required=True)
    parser.add_argument("--process-id", type=int, required=True)
    parser.add_argument("--started", type=int, required=True)
    parser.add_argument("--target", type=Path, required=True)
    parser.add_argument("--current-process-id", type=int, required=True)
    parser.add_argument("--result", type=Path, required=True)
    options = parser.parse_args()
    try:
        with (options.target.resolve() / ".tkfm-first-run-import.lock").open("a+b"):
            outcome = main(options)
    except Exception as error:
        outcome = {"ok": False, "error": str(error)}
    options.result.write_text(json.dumps(outcome, ensure_ascii=False, indent=2), encoding="utf-8")
    sys.exit(0 if outcome["ok"] else 1)
